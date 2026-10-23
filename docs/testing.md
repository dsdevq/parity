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
| #19 constants and resource texts unresolved | `PremiumHistoryForm` reading `PolicyLimits` consts, `Captions` keys (one missing, `Captions.fr.resx`), grid bound to annotated `PremiumHistoryRow` | `IssueTests.issue_19_constants_and_resource_texts_are_resolved_with_their_values_and_a_missing_key_is_diagnosed`, `IssueTests.issue_19_a_resource_key_with_a_space_resolves_when_only_this_form_is_extracted` |
| #20 tenant variants described as if they were the base | `Tenants/Larkspur/LarkspurClientSearchForm` (reads `LarkspurLimits.MaxResults` where the base reads `SearchLimits.MaxResults`), `SampleLegacy.Thistle` (a second csproj), `Forms/ClientSearchFormFactory.cs`; `variants` in `fixtures/extract.config.json` | `IssueTests.issue_20_tenant_variants_fold_into_the_base_manifest_with_a_delta_and_factories` (deltas including `constant`, factory cases and guarded factory branches, Variant sections, `VAR-*` lint), `IssueTests.issue_20_a_configured_factories_entry_is_a_type_or_a_type_dot_method_and_one_that_matches_nothing_is_diagnosed`, `IssueTests.issue_20_a_configured_subclass_that_derives_from_no_matched_form_is_diagnosed_not_dropped`, `IssueTests.issue_20_variants_of_a_form_the_glob_does_not_select_are_not_a_config_error`, `IssueTests.issue_20_a_base_chain_that_leaves_the_main_project_is_left_to_the_compiler` (a base from a referenced assembly, a wildcard `formBaseTypes` entry) |
| #21 spec cannot cite a fact outside the manifest | `ClientDto` (`Services/Dto.cs:21`) behind `BeneficiarySearchForm.Setup` | `IssueTests.issue_21_a_spec_cites_evidence_outside_the_manifest_and_lint_reports_it_stale_when_the_source_moves` |
| #22 guard coverage: shell tool names, MCP tools, root-substring bypass | - | `IssueTests.issue_22_guard_allow_lists_readable_roots_denies_code_index_tools_and_logs_every_denial` (hook JSON through `sh` in a temp repo with the legacy tree beside it; asserts the settings matcher, the built-in MCP deny-list (Graft by server name `graft` and by the `graft_` tool prefix under any server), the allow-list in both restricted phases, `extract` unguarded, `guard.denyTools` / `guard.allowRoots`, Windows spellings of the root, no-jq refusal and one log line per denial; skipped where no POSIX shell exists) |
| #24 synthetic fixtures indistinguishable from recorded ones | the example spec's `svc:IPolicyService.GetPremiumLimits` (`PolicyWizardStep3`), recorded only by a hand-made synthetic reply; then a variant scaffolded from `limits-term-life` | `IssueTests.issue_24_a_rule_covered_only_by_synthetic_fixtures_is_warned_an_unverified_synthetic_fixture_is_an_error_and_scaffold_labels_its_output` |
| #25 `STALE` is a dead end after a re-extract | `PolicyWizardStep3` re-extracted with an extra `formBaseTypes` entry, a moved handler, a changed callee under a handler-only citation, IPolicyService out of the boundary, then a lower `maxCallDepth` | `IssueTests.issue_25_a_re_extract_keeps_the_previous_manifest_and_rebind_rewrites_the_hash_only_when_nothing_cited_changed` |
| #23 adoption is a long manual checklist; skills point at a template the consuming repo lacks | a temp consuming repo built from a copy of `fixtures/SampleLegacy`, already holding its own `.claude/settings.json` (a foreign PreToolUse and Stop hook, a permission) and a skill of its own (`tests/Parity.Tests/ConsumingRepo.cs`) | `IssueTests.issue_23_init_lays_out_the_consuming_repo_and_merges_settings_without_clobbering` (layout and config skeleton from the csproj, `--merge` keeps the foreign hooks first and in order, issue 22's matcher unchanged, idempotent second run, drift reported until `--merge`, a `settings.json` with comments is not rewritten (refused before anything is written while it lacks a hook, accepted once the hooks are added by hand), duplicate keys refused, embedded kit equals the repo's `adopt/` and `templates/`), `IssueTests.issue_23_the_installed_skills_are_self_contained` (spec template beside the skill, every file a skill names exists, every command a skill gives runs through `sh` with `PARITY_BIN` set to a stub and nothing named `parity` on PATH and reaches the stub, and with `PARITY_BIN` unset reaches `parity` on PATH), `IssueTests.issue_23_doctor_prints_a_line_per_check_and_exits_non_zero_on_a_fail` (a controlled PATH; each check broken in turn, the >200 DLL warning with 201 real assemblies, git-ignore; skipped where no POSIX shell exists), `IssueTests.issue_23_phase_driver_runs_each_phase_in_a_fresh_session_with_its_prompt_and_phase` (a stand-in agent logs pid, `.phase` and prompt; one process per phase, a failure stops the run; skipped where no POSIX shell exists) |

## Adding a pattern

1. Add or extend a form under `fixtures/SampleLegacy/Forms` with invented names, and list its files in
   `fixtures/SampleLegacy/SampleLegacy.csproj`. If it needs a control type the stubs lack, add the type
   to `fixtures/WinFormsStubs` with only the members the form touches.
2. `dotnet build fixtures/SampleLegacy.Build` must stay green: the fixture is compiled so that
   `selfAssembly` is exercised like at work.
3. Write the test first: what the manifest must contain for that form. Then change the extractor.
4. `every_form_has_no_raw_statements_and_only_the_intended_diagnostics` lists the diagnostics each
   form is allowed to have; a new intended diagnostic goes into that table with its reason.
