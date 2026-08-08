---
version: "1.0.0"
level: copilot
processes:
  design: pair
  implementation: copilot
  testing: copilot
  documentation: copilot
  review: pair
---

This format is based on [AI-DECLARATION.md](https://ai-declaration.md/).

## Notes

- Developed with Claude Code (Anthropic) throughout — most implementation, refactors, and documentation were AI-written from human-provided direction and requirements.
- The human author (Nahuel Nillni) planned features, reviewed and tested every change before it shipped, and made all design/architecture decisions and tradeoff calls.
- No change was merged without a human building and running it first — see the project's own "always compile after C# changes" workflow convention.
