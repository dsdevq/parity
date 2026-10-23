# Prior art

Checked 2026-10-22. None of these were adopted; the reasons are specific.

| Tool | What it does | Why not the base |
|---|---|---|
| WinFormsDesignerMcp (MIT, `dotnet tool`, 1.0.x) | Roslyn parse of `.Designer.cs`: control hierarchy, properties, event wiring; JSON, SVG wireframes, MCP server | no resx, no DevExpress layout-item tree, no handler bodies or call graph, no stable ids with locations; its JSON would become the citation contract without the fields the lint needs |
| Easy2Work.LegacyModernizer (MIT, 0.1.0) | Roslyn over a whole `.sln`: forms, event handlers, dependency graph, "business rules" from `@BusinessRule` comments | requires loading the solution, which an 800-project solution rules out; no designer control tree or bindings; comment-annotated rules do not exist in a legacy codebase |
| WinFormDiscoverSkill | Visual Studio COM automation: designer screenshots plus markdown per form | needs VS on Windows; useful as a separate verify-phase aid for side-by-side screenshots, not as the extractor |
| ast-grep, Semgrep | syntax pattern search, no build | the reconnaissance tool (`parity probe` covers the designer part); no semantics, no tree |
| CodeQL, Joern | code property graphs | CodeQL is licensed for private commercial code only with GitHub Advanced Security; Joern's C# frontend is young; both are a project to stand up for a 50M-line solution |
| NDepend, Understand | dependency graphs | commercial; project-level, not form-level; a one-time blast-radius map at most |
| Mobilize WebMAP and similar | WinForms to web conversion | transliterates control for control, which reproduces the mess; cannot target an existing proxy backend |
| ILSpy, dnSpy | decompilers | irrelevant with source; kept on the shelf for a compiled-only dependency that hides a rule |
| Graft (Nanonets) | tree-sitter context graph with LLM-written summary nodes, MCP | navigation for everything outside the module; summaries are not citeable and must not sit next to manifests |

Roslyn itself is the parser; everything here is the few hundred lines around it that know what a form is.
