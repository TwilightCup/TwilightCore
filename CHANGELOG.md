# Changelog

This file contains user-facing release notes for TwilightCore. Only changes that plugin users can observe belong here.

## 0.0.0

- **Release Date:** Unreleased
- **Highlights:** _To be filled during version branch preparation._
- **Details:**
  - Fixed round time reporting: per-level times, attempt skips, round completion and forfeit signals now carry the UTC timestamp the match server requires, so they are accepted again (they were being rejected with `400: Malformed message`) and level times sync correctly.
  - The live timer sync and subsegment tracking are no longer disabled for the rest of a round by a single unrelated server error.
- **Contributors:** _To be filled from PRs merged into dev._
