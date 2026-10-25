"""The second implementation the connect check vectors in ConnectCheckTests.cs came from (CONTRACT-DIRECT.md section 3a).

Not part of the build and not a test: it is here so that anyone can recompute the vectors the C# test asserts, with a method that
shares no code with the library. Run it with:

    python ConnectCheckVectors.py

The rule: d = SHA-256(UTF-8("BYOA-CHECK-1:" + session)); character i (0 to 3) is alphabet[d[i] mod 31].
"""
import hashlib

ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"          # no I, L, O, 0, 1

# the sessions of the test, and what the code of each one must be
VECTORS = [
    ("", "G7CP"),
    ("a", "G3Y6"),
    ("test-session-0001", "PW4A"),
    ("V8lRbzS0m6b-9A0vV8lRbzS0m6b-9A0v", "88UT"),     # a 32-character URL-safe code: the shape a site issues
    ("kQ3Zr8Yt1pLd5Nw7Xc2Vb9Mj4Hf6Gs0A", "QZTP"),
    ("0123456789abcdefghijklmnopqrstuvwxyz", "X5N7"),
    ("SESSION-é中\U0001f600", "P546"),       # not ASCII: the digest is over the UTF-8 bytes
]


def check(session: str) -> str:
    digest = hashlib.sha256(("BYOA-CHECK-1:" + session).encode("utf-8")).digest()
    return "".join(ALPHABET[digest[i] % len(ALPHABET)] for i in range(4))


if __name__ == "__main__":
    for session, expected in VECTORS:
        got = check(session)
        shown = session.encode("unicode_escape").decode("ascii")      # the Windows console is not UTF-8
        print(f"{'ok  ' if got == expected else 'FAIL'} {got}  {shown}")