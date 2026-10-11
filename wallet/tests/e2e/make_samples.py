"""Make the speech clips the end-to-end test sends through the wallet: 16 kHz mono 16-bit WAVs.

Single country names in Russian and Slovak are synthesised with Piper voices through sherpa-onnx (offline);
longer Russian sentences can be added from a folder of recorded WAVs that have a .txt transcript next to them.

    python make_samples.py <out dir> --voice ru=<piper voice dir> --voice sk=<piper voice dir> [--recorded <folder>]

A Piper voice dir holds <name>.onnx, tokens.txt and espeak-ng-data (the sherpa-onnx "vits-piper-*" archives).
Writes <out dir>/<lang>-word-<n>.wav, ru-sentence-*.wav and samples.json: [{"file", "lang", "expect", "kind", "seconds"}].
Needs: sherpa-onnx, numpy.
"""
import argparse
import json
import pathlib
import wave

import numpy as np

WORDS = {  # Russian names are written as escapes: the repository is English and Latin script only
    "ru": ["\u0424\u0440\u0430\u043d\u0446\u0438\u044f", "\u0413\u0435\u0440\u043c\u0430\u043d\u0438\u044f", "\u042f\u043f\u043e\u043d\u0438\u044f", "\u0411\u0440\u0430\u0437\u0438\u043b\u0438\u044f", "\u0421\u043b\u043e\u0432\u0430\u043a\u0438\u044f", "\u0418\u0442\u0430\u043b\u0438\u044f", "\u0415\u0433\u0438\u043f\u0435\u0442", "\u0410\u0440\u0433\u0435\u043d\u0442\u0438\u043d\u0430"],
    "sk": ["Francúzsko", "Nemecko", "Taliansko", "Španielsko", "Maďarsko", "Rakúsko", "Poľsko", "Japonsko"],
}
RATE = 16000


def to_16k(samples, rate):
    samples = np.asarray(samples, dtype=np.float32)
    if rate == RATE:
        return samples
    n = int(len(samples) * RATE / rate)
    x = np.linspace(0, len(samples) - 1, n)
    return np.interp(x, np.arange(len(samples)), samples).astype(np.float32)


def write_wav(path, samples, pad_seconds=0.6):
    pad = np.zeros(int(RATE * pad_seconds), dtype=np.float32)
    data = np.concatenate([pad, samples, pad])
    pcm = (np.clip(data, -1, 1) * 32767).astype("<i2").tobytes()
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm)
    return len(data) / RATE


def piper(voice_dir):
    import sherpa_onnx
    d = pathlib.Path(voice_dir)
    model = next(p for p in d.glob("*.onnx"))
    config = sherpa_onnx.OfflineTtsConfig(
        model=sherpa_onnx.OfflineTtsModelConfig(
            vits=sherpa_onnx.OfflineTtsVitsModelConfig(
                model=str(model), tokens=str(d / "tokens.txt"), data_dir=str(d / "espeak-ng-data")),
            num_threads=2, provider="cpu"),
        max_num_sentences=1)
    tts = sherpa_onnx.OfflineTts(config)

    def say(text, speed=1.0):
        audio = tts.generate(text, sid=0, speed=speed)
        return to_16k(np.array(audio.samples, dtype=np.float32), audio.sample_rate)
    return say


def read_wav(path):
    with wave.open(str(path)) as w:
        raw = w.readframes(w.getnframes())
        data = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768
        if w.getnchannels() > 1:
            data = data.reshape(-1, w.getnchannels()).mean(axis=1)
        return to_16k(data, w.getframerate())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--voice", action="append", default=[], help="lang=<piper voice dir>; repeat for more voices")
    ap.add_argument("--recorded")
    a = ap.parse_args()
    out = pathlib.Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    voices = {}
    for spec in a.voice:
        lang, path = spec.split("=", 1)
        voices.setdefault(lang, []).append((pathlib.Path(path).name, piper(path)))
    items = []
    for lang, words in WORDS.items():
        if lang not in voices:
            continue
        for i, word in enumerate(words):
            name, say = voices[lang][i % len(voices[lang])]
            path = out / f"{lang}-word-{i + 1}.wav"
            seconds = write_wav(path, say(word, speed=0.9))
            items.append({"file": path.name, "lang": lang, "expect": word, "kind": "word", "voice": name, "seconds": round(seconds, 2)})
    if a.recorded:
        for src in sorted(pathlib.Path(a.recorded).glob("*.wav")):
            txt = src.with_suffix(".txt")
            if not txt.exists():
                continue
            samples = read_wav(src)
            if len(samples) / RATE > 14:
                continue
            path = out / f"ru-sentence-{src.stem}.wav"
            seconds = write_wav(path, samples, pad_seconds=0.2)
            items.append({"file": path.name, "lang": "ru", "expect": txt.read_text(encoding="utf-8").strip(), "kind": "sentence", "seconds": round(seconds, 2)})
    (out / "samples.json").write_text(json.dumps(items, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{len(items)} clips in {out}")


if __name__ == "__main__":
    main()
