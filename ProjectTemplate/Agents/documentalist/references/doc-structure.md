### `doc/` folder structure

The `doc/` folder is the single home for architecture documentation. It MUST follow this layout:

```
doc/
  index.md           # entry point — lists every feature/system with a one-line summary and a link
  <feature>.md       # one file per major feature or system
  <other>.md
  ...
```

Rules for `doc/`:

- **`doc/index.md` is mandatory** as soon as `doc/` contains at least one feature file. It is the table of contents: every feature doc must be listed there with a short description and a relative link (`./<feature>.md`).
- **One file per feature/system.** A "feature" is a coherent unit of behavior (e.g. `automation-engine.md`, `agent-dispatch.md`, `project-registry.md`, `kanban-ui.md`). Do not bundle unrelated features.
- **Each feature file MUST cover, in this order:**
  1. **Purpose** — what the feature does and why it exists.
  2. **Key components** — the classes, services, modules, or files that implement it.
  3. **Entry points** — how the feature is invoked (HTTP endpoint, CLI command, UI action, automation trigger, …).
  4. **External dependencies** — other features, libraries, services, databases, or APIs it relies on.
- **No duplication.** A given concept is explained in exactly one feature file. Other files that touch it must **link** to that file via a relative markdown link (e.g. `see [automation engine](./automation-engine.md#triggers)`) rather than re-explaining it.
- **Cross-references use relative links.** Never hardcode absolute paths or URLs to local files.
- **Filenames** are lowercase-kebab-case, ASCII only, no spaces.

You do **NOT** touch:
- Source code, configuration, or test files.
- Agent skills, memories, or `.agents/**` content (those have their own owners).
- Ticket comments (the `committer` and other agents handle those).
- License, contributing, or governance files (`LICENSE`, `CODE_OF_CONDUCT.md`, etc.) unless the owner explicitly asks.
