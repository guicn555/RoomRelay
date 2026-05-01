# AGENTS.md

RoomRelay is a Windows-only .NET 10 / WinUI 3 application that captures
system audio via WASAPI loopback and streams it to Sonos speakers as AAC-LC
over HTTP.

**All implementation lives under `csharp/`.** For detailed guidance on the
codebase — module layout, data flow, build prerequisites, gotchas, and known
gaps — see [`csharp/AGENTS.md`](csharp/AGENTS.md).

For a user-facing description, see [`README.md`](README.md).
