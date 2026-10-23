# Testing

`dotnet test` runs the suite over `fixtures/SampleLegacy`, a synthetic legacy project that reproduces the
patterns reported from real codebases. Every extraction test states its input (a fixture form) and its
expected output (ids, categories, parents, counts); every lint test mutates the example spec and names
the codes it expects. The fixture is the contract: a new real-world pattern is first added there as a
form with invented names, then the extractor is changed until the test passes.

## Issues to tests

| Issue | Pattern in the fixture | Test |
|---|---|---|
| #1 `ImageList.Images.SetKeyName` raw | `BeneficiarySearchForm.designer.cs` `imgIcons` | `ExtractTests.names_the_designer_uses_but_never_creates_are_resolved_not_diagnosed` (`Images.SetKeyName()` prop) |
| #2 designer locals diagnosed | `editorButtonImageOptions1` local | same test: category `local` |
| #3 ListView `ColumnHeader` fields | `_lstResults_ColumnHeader_1/2` created in the constructor | same test: category `column`, parent `lstResults`, `@declaredOnly`, no `dyn:` duplicate |
| #4 members inherited from a custom base form | `BaseDialogForm` with `btnOK`, `btnCancel`, `pnlMain`; `this.Appearance.BackColor`, `this.IconOptions.ShowIcon` | same test: `@inherited`, form props |
| #5 `UNREACHED-METHOD` without detail | `btnExport_Click`, never wired | `ExtractTests.unreached_method_diagnostic_says_what_was_checked` |
| #6 probe misses `.designer.cs` | `BeneficiarySearchForm.designer.cs` (lowercase) | `IssueTests.issue_6_probe_matches_a_lowercase_designer_companion_by_exact_form_name` |
| #7 references reloaded per round, no timing, solution-wide globs | - | `ProjectTests.references_can_be_derived_from_the_csproj_items`, `IssueTests.issue_7_references_load_once_and_each_phase_is_timed` |
| #8 CRLF breaks tests and slices | - | `ExtractTests.slices_never_contain_carriage_returns`, `IssueTests.issue_8_a_spec_with_crlf_line_endings_lints_identically`; `.gitattributes` forces LF |
| #9 guard hook fails open without jq | - | `GuardHookTests.issue_9_guard_fails_closed_without_a_root_and_without_jq` (runs the script under `sh` with jq hidden; skipped where no POSIX shell exists) |
| #10.3 anonymization claim | - | `IssueTests.issue_10_report_contains_no_identifier_from_the_codebase`, `issue_10_report_anonymizes_every_diagnostic_shape_the_extractor_emits` |
| #10.6 verifying `formBaseTypes` | - | `IssueTests.issue_10_an_unresolvable_form_base_type_is_a_diagnostic_and_a_glob_that_matches_no_form_explains_itself` |
| #10.8 output path | - | `issue_6_...` asserts the probe prints `outputPath:` and reference counts |
| #10.1, .2, .4, .5, .7 | documentation and `bin/parity.cmd` | not testable; README handoff section |
| #11 `Controls.SetChildIndex` raw | `ClientSearchForm.Designer.cs` z-order lines | `IssueTests.issue_11_set_child_index_is_the_control_s_z_order_not_raw` |
| #12 module.json overwritten per run | - | `IssueTests.issue_12_module_json_accumulates_across_runs_and_globs_take_a_comma_list` |
| #13 one debt line acknowledged a whole code | - | `IssueTests.issue_13_a_debt_line_acknowledges_one_diagnostic_not_a_whole_code` |
| #14 public setup methods not sliced | `BeneficiarySearchForm.Setup(...)` calling a private helper | `IssueTests.issue_14_non_private_methods_are_entry_points_with_slices_and_their_callees_are_reached` |
| #15 grid without designer columns is silent | `gvHistory`, columns built in `InitHistoryColumns` | `IssueTests.issue_15_a_grid_view_without_designer_columns_is_a_named_diagnostic_pointing_at_the_code_that_builds_them` |
| #16 `COV-TEST` satisfied by `it.todo` / comments | - (test writes an implement dir) | `IssueTests.issue_16_a_rule_id_in_a_todo_skip_comment_or_constant_is_not_a_test` |
| #21 spec cannot cite a fact outside the manifest | `ClientDto` (`Services/Dto.cs:21`) behind `BeneficiarySearchForm.Setup` | `IssueTests.issue_21_a_spec_cites_evidence_outside_the_manifest_and_lint_reports_it_stale_when_the_source_moves` |
| #22 guard coverage: shell tool names, MCP tools, root-substring bypass | - | `IssueTests.issue_22_guard_allow_lists_readable_roots_denies_code_index_tools_and_logs_every_denial` (hook JSON through `sh` in a temp repo with the legacy tree beside it; asserts the settings matcher, the allow-list in both restricted phases, `extract` unguarded, `guard.denyTools` / `guard.allowRoots`, Windows spellings of the root, no-jq refusal and one log line per denial; skipped where no POSIX shell exists) |
| #25 `STALE` is a dead end after a re-extract | `PolicyWizardStep3` re-extracted with an extra `formBaseTypes` entry, a moved handler, a changed callee under a handler-only citation, IPolicyService out of the boundary, then a lower `maxCallDepth` | `IssueTests.issue_25_a_re_extract_keeps_the_previous_manifest_and_rebind_rewrites_the_hash_only_when_nothing_cited_changed` |

## Adding a pattern

1. Add or extend a form under `fixtures/SampleLegacy/Forms` with invented names, and list its files in
   `fixtures/SampleLegacy/SampleLegacy.csproj`. If it needs a control type the stubs lack, add the type
   to `fixtures/WinFormsStubs` with only the members the form touches.
2. `dotnet build fixtures/SampleLegacy.Build` must stay green: the fixture is compiled so that
   `selfAssembly` is exercised like at work.
3. Write the test first: what the manifest must contain for that form. Then change the extractor.
4. `every_form_has_no_raw_statements_and_only_the_intended_diagnostics` lists the diagnostics each
   form is allowed to have; a new intended diagnostic goes into that table with its reason.
