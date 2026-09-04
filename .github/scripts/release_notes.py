"""Print the release notes for one version: the matching section of CHANGELOG.md
without its heading, followed by the install line.

Fails when the section is missing, so a tag cannot publish a release with no
notes; writing the changelog section is part of cutting a release.
"""
import re
import sys

version = sys.argv[1]
text = open("CHANGELOG.md", encoding="utf-8").read()

pattern = re.compile(
    r"^## \[" + re.escape(version) + r"\][^\n]*\n(.*?)(?=^## \[|\Z)",
    re.MULTILINE | re.DOTALL,
)
match = pattern.search(text)
if not match:
    sys.stderr.write(f"CHANGELOG.md has no section for [{version}]\n")
    sys.exit(1)

notes = match.group(1).strip()
tag = f"v{version}"
notes += (
    "\n\n### Install\n\n"
    "In the Package Manager, **Add package from git URL**:\n\n"
    f"```\nhttps://github.com/towneh/vrsl-urp.git#{tag}\n```\n\n"
    "Or download the tarball below and use **Add package from tarball**. "
    "AudioLink must be in the project first; see the "
    "[wiki](https://github.com/towneh/vrsl-urp/wiki/Getting-Started)."
)
print(notes)
