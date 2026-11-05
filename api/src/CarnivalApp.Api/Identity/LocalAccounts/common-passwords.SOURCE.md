# common-passwords.txt

Common and breached passwords from SecLists' `Pwdb_top-1000000.txt`, most common first, used offline to refuse them for local accounts: the 100,000 most common of at least 8 characters, then the next most common of at least 15 characters until 3,000 entries of 15 or more are listed (102,578 entries).

- Source: https://github.com/danielmiessler/SecLists/blob/913b327317496d062bcc7cace524aaad8a693be2/Passwords/Common-Credentials/Pwdb_top-1000000.txt
- Commit: `913b327317496d062bcc7cace524aaad8a693be2` (2026-09-08)
- Licence: MIT (Copyright (c) 2018 Daniel Miessler), https://github.com/danielmiessler/SecLists/blob/913b327317496d062bcc7cace524aaad8a693be2/LICENSE
- Source file SHA-256: `e9a88f67aafe65496682dc374559ee714e978bee50314767494c3e37a18c9fc8`
- This file's SHA-256: `39d0b64d7ba44002f297058bf37ef0502a7a77f67f35e1c99527d90b4d9e3650`

Filter (lengths are Unicode code points; dropped: lines with leading or trailing whitespace, unprintable characters, U+FFFD or UTF-8-read-as-Latin-1 mojibake):

```python
import re
MOJIBAKE = re.compile("[ÂÃ][\u0080-¿]|â€|�")
lines = open("Pwdb_top-1000000.txt", encoding="utf-8", errors="replace").read().split("\n")
eight = [l for l in lines if len(l) >= 8 and l == l.strip()
         and not MOJIBAKE.search(l) and all(c.isprintable() for c in l)]
out, long_total = eight[:100000], sum(len(l) >= 15 for l in eight[:100000])
for l in eight[100000:]:
    if long_total >= 3000: break
    if len(l) >= 15 and l not in out:
        out.append(l); long_total += 1
open("common-passwords.txt", "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
```
