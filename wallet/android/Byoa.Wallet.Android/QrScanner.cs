using AndroidActivity = global::Android.App.Activity;
using Android.Content;
using Android.Gms.Tasks;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;
using Uri = Android.Net.Uri;

namespace Byoa.Wallet.Android;

/// <summary>
/// "Scan a QR code": Google's code scanner (Google Play services) shows its own camera screen and hands back the text of the code, so the wallet asks for no
/// camera permission and sees no picture. What it read goes through <see cref="WalletAddress.Read"/>: a link (the web wallet's address of a site's QR code,
/// or a <c>bringyourownagent://connect?</c> link) goes to the wallet as a handed link and is asked about on the consent page; a site's hand-off address is
/// opened in the browser (it is the site's page); anything else is said plainly to be nothing, and nothing is connected.
/// </summary>
static class QrScanner
{
    public static void Scan(AndroidActivity activity, AndroidServices wallet)
    {
        try
        {
            var options = new GmsBarcodeScannerOptions.Builder().SetBarcodeFormats(Barcode.FormatQrCode).Build();
            var scanner = GmsBarcodeScanning.GetClient(activity, options);
            var task = scanner.StartScan();
            task.AddOnSuccessListener(new Done(text => Read(activity, wallet, text)));
            task.AddOnFailureListener(new Failed(wallet));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write(e);
            wallet.Ui.NoteLine("The scanner could not start on this phone. Open the site's link by hand: copy it and paste it under Connect by link.");
        }
    }

    static void Read(AndroidActivity activity, AndroidServices wallet, string? text)
    {
        var reading = WalletAddress.Read(text);
        switch (reading.Kind)
        {
            case CodeKind.Link:
                wallet.OpenHandedOrSay(reading.Value);
                break;
            case CodeKind.HandOff:
                try
                {
                    activity.StartActivity(new Intent(Intent.ActionView, Uri.Parse(reading.Value)!));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    ErrorLog.Write(e);
                    wallet.Ui.NoteLine("No browser opened that address. Open the site in a browser and use its own button.");
                }

                break;
            default:
                wallet.Ui.NoteLine(reading.Line ?? WalletAddress.NotASiteLine);
                break;
        }
    }

    sealed class Done(Action<string?> onText) : Java.Lang.Object, IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            try
            {
                onText((result as Barcode)?.RawValue);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ErrorLog.Write(e);
            }
        }
    }

    sealed class Failed(AndroidServices wallet) : Java.Lang.Object, IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception e)
        {
            // the visitor closing the scanner is a cancel, not a failure: the scanner reports it as a failure with its own code
            var message = e.Message ?? "";
            if (message.Contains("canceled", StringComparison.OrdinalIgnoreCase) || message.Contains("cancelled", StringComparison.OrdinalIgnoreCase)) return;
            ErrorLog.Write(new InvalidOperationException("The code scanner failed: " + message));
            wallet.Ui.NoteLine("The scanner could not read a code (" + message + "). Open the site's link by hand: copy it and paste it under Connect by link.");
        }
    }
}
