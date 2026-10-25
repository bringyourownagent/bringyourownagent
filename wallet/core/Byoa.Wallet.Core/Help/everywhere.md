# Everywhere: dictation and the check of selected text

The wallet is for sites, and also for you. The page **Everywhere** gives your own models two jobs in every program on your computer, with no website in between. Both are **off** until you turn them on, because they listen for a hotkey in every program and send keystrokes to the program you are working in.

## Dictation into any text field

Press the hotkey (Ctrl+Alt+Space to begin with), say what you want to write, and the wallet types the words into the field that has the focus: a browser, an editor, a chat. A small indicator in the corner says "Listening…", then "Writing…", and shows a plain message if something goes wrong.

- **Hold to talk**: it records while you hold the hotkey, and writes when you let go. **Press to start, press to stop**: one press starts, the next one stops.
- **Esc** drops a recording, or stops waiting for the words.
- A recording ends by itself after 60 seconds, and what you said until then is written.
- **Language**: automatic (the speech model finds it) or one you choose. A fixed language helps a small model.
- The sound goes to the speech model of your own route (see My models and Skills & fallbacks), exactly as a site's sound would, and to nothing else. Nothing is recorded unless dictation is on **and** you press its hotkey. The wallet does not keep the sound or the words; the Activity page shows only that a call was made.
- The words are put into the program with a paste: the wallet saves what is on the clipboard, puts the words there (kept out of the clipboard history), sends Ctrl+V, and puts your old clipboard back. A paste brings a long text, line breaks and every letter whole, which typing the characters one by one does not. If the clipboard is busy, the wallet types the characters instead.

## The check of selected text

Select text anywhere, press the second hotkey (Ctrl+Alt+K to begin with). The wallet copies the selection, asks your own text model to do one **action** on it, and shows the answer in a small window near the cursor with **Copy**, **Replace selection** (it goes back to the program and pastes the answer over the selection) and **Close**.

- The actions are yours: a name and an instruction each. There are two to begin with, "Check facts and wording" and "Translate to English". Edit them on the Everywhere page. With one action it runs at once; with several, the window shows a row of buttons, and a second press of the hotkey runs the first.
- It asks the first text model of your route that answers, and never one of your agents. The text goes to that model only. It is never logged.
- It does not copy from a terminal window: Ctrl+C there would stop the program that runs in it.
- A selection up to 20,000 characters is taken.

## Models, limits and money

Everywhere uses your routes and your limits like a site does. Its calls are in the Activity page under the name **Everywhere**, and count in the wallet's limits for the day. By default it uses only models on this computer or on a home server. If you tick **Let Everywhere use a paid source**, a paid source of your route may answer when no model of yours does, and the wallet asks before each paid call.

If no speech model or no text model is set up, the window says so in plain words: add one in My models.

## Hotkeys

You can change either hotkey. A hotkey needs Ctrl or Alt with a key (it would stop you typing the key otherwise), and cannot be one that Windows or most programs use (Ctrl+C, Alt+F4 and the like), or one the other feature uses. The wallet also asks Windows whether another program already holds the hotkey. If you use another dictation program, choose a different hotkey for one of them: the wallet tells you which hotkey clashes and does not listen to it until you choose another. On a keyboard where AltGr writes letters, avoid Ctrl+Alt with a letter.

The wallet cannot see a program that listens to the keyboard in its own way, so if a hotkey does nothing, or does two things, another program is probably using it too.
