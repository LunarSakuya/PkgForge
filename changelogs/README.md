# Versioned release notes

Each release has one file named after its exact release version:

```text
changelogs/<major>.<minor>.<patch>.md
```

The active release is the full value in [`VERSION`](../VERSION). The release
workflow reads only that exact file. It does not parse the index, concatenate
history, or search for the newest section.

Rules:

- Create or update the file before pushing the release commit.
- Keep the content specific to that release.
- Do not add a version heading; the release title comes from `VERSION`.
- Do not leave placeholder text in a release file.
- Historical range names are allowed for older grouped entries.

`scripts/version.ps1` creates a skeleton file for a new version when one does
not already exist. Fill it in before committing.
