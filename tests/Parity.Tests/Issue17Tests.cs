using System.Diagnostics;
using System.Text.Json;
using Parity.Lint;
using Parity.Model;
using Parity.Passes;
using Parity.Tools;
using Xunit;

namespace Parity.Tests;

/// <summary>Issue #17: the context around a unit - its contract, shared state, hosts, dependencies, scenarios and decisions.</summary>
public sealed partial class IssueTests
{
    private const string StoreChoice = "method:SampleLegacy.Forms.BeneficiaryHostForm.StoreChoice(SampleLegacy.Services.ClientDto)";
    private const string Create = "method:SampleLegacy.Forms.ClientSearchFormFactory.Create(string)";

    [Fact]
    public void issue_17_a_unit_contract_lists_entries_including_reflection_inputs_from_the_host_and_outputs_written_back()
    {
        // input: CoverSummaryForm.ApplyDefaults is private and only reached through typeof(...).GetMethod("ApplyDefaults").Invoke in
        // BeneficiaryHostForm; BeneficiarySearchForm takes its inputs through Setup and hands back Selected; PolicyWizardStep3 reads
        // and calls the controller its ctor is given. output: a contract per unit, with each value's reader or writer
        var cover = _f.Manifest("CoverSummaryForm");
        Assert.DoesNotContain(cover.Diagnostics, d => d.Code == "UNREACHED-METHOD");     // was: named in a string ... (reflection?)
        var apply = Assert.Single(cover.Handlers, h => h.Name == "ApplyDefaults");
        Assert.Equal("reflection", apply.Kind);
        Assert.Equal(new[] { "evt:CoverSummaryForm/$this.ApplyDefaults" }, apply.Events);
        var c = cover.Contract!;
        var entry = Assert.Single(c.Entries, x => x.Kind == "reflection");
        Assert.Equal("handler:CoverSummaryForm/ApplyDefaults", entry.Handler);
        Assert.Equal("ApplyDefaults()", entry.Signature);
        Assert.Equal(new[] { "Forms/BeneficiaryHostForm.cs:44" }, entry.InvokedFrom);
        Assert.Contains(c.Entries, x => x.Kind == "ctor" && x.Handler == "handler:CoverSummaryForm/ctor");
        Assert.Equal(new[] { "handler:CoverSummaryForm/ApplyDefaults" }, Assert.Single(c.Inputs, x => x.Kind == "state" && x.Name == "WizardSession.BeneficiaryName").Via);
        Assert.Contains(c.Inputs, x => x.Kind == "state" && x.Name == "WizardSession.PolicyId" && x.Type == "int");
        var result = Assert.Single(c.Outputs, x => x.Kind == "dialog-result");
        Assert.Equal(new[] { "OK" }, result.Values);
        Assert.Equal(new[] { "handler:CoverSummaryForm/btnConfirm_Click" }, result.Via);

        var search = _f.Manifest("BeneficiarySearchForm").Contract!;
        var setup = Assert.Single(search.Entries, x => x.Handler == "handler:BeneficiarySearchForm/Setup");
        Assert.Equal(("entry", "Setup(int policyId, bool allowDeceased, SampleLegacy.Services.ClientDto preselected)"), (setup.Kind, setup.Signature));
        Assert.Null(setup.InvokedFrom);
        Assert.Equal(new[] { "Setup.policyId", "Setup.allowDeceased", "Setup.preselected" }, search.Inputs.Where(x => x.Kind == "param").Select(x => x.Name));
        var selected = Assert.Single(search.Outputs, x => x.Kind == "property");
        Assert.Equal(("Selected", "SampleLegacy.Services.ClientDto"), (selected.Name, selected.Type));
        Assert.Equal(new[] { "handler:BeneficiarySearchForm/OnAccept" }, selected.Via);
        Assert.Equal(new[] { "OK" }, Assert.Single(search.Outputs, x => x.Kind == "dialog-result").Values);

        var host = _f.Manifest("BeneficiaryHostForm").Contract!;
        Assert.Equal(new[] { "AllowDeceased", "TenantCode" }, host.Inputs.Where(x => x.Kind == "property").Select(x => x.Name));
        Assert.Equal(new[] { "handler:BeneficiaryHostForm/btnChoose_Click" }, Assert.Single(host.Inputs, x => x.Name == "AllowDeceased").Via);
        Assert.Equal(new[] { "WizardSession.BeneficiaryId", "WizardSession.BeneficiaryName", "WizardSession.Changes" }, host.Outputs.Where(x => x.Kind == "state").Select(x => x.Name));
        Assert.DoesNotContain(host.Outputs, x => x.Kind == "dialog-result");

        var step3 = _f.Manifest("PolicyWizardStep3").Contract!;
        Assert.Contains(step3.Inputs, x => x.Kind == "param" && x.Name == "ctor(2).controller" && x.Type == "SampleLegacy.Forms.PolicyWizardController");
        Assert.Contains(step3.Inputs, x => x.Kind == "host" && x.Name == "_controller.CanGoBack" && x.Via.Contains("handler:PolicyWizardStep3/ctor(2)"));
        Assert.Contains(step3.Inputs, x => x.Kind == "host" && x.Name == "_policy.Premium");
        Assert.Equal(new[] { "_controller.Back()", "_controller.Next()" }, step3.Outputs.Where(x => x.Kind == "host-call").Select(x => x.Name));
        var riders = _f.Manifest("DynamicRidersForm").Contract!;
        Assert.Contains(riders.Outputs, x => x.Kind == "host" && x.Name == "_policy.Riders");          // written on the object the ctor was handed

        // the markdown carries the contract for a reader of the manifest
        var md = File.ReadAllText(Path.Combine(_f.ModuleDir, "manifests", "CoverSummaryForm.md"));
        Assert.Contains("## Contract", md);
        Assert.Contains("reflection `handler:CoverSummaryForm/ApplyDefaults`", md);
    }

    [Fact]
    public void issue_17_b_shared_state_reads_and_writes_are_recorded_per_unit_transitively_and_module_json_graphs_producers_and_consumers()
    {
        // input: sharedState ["SampleLegacy.Session.*"]; the host writes WizardSession in StoreChoice (a callee of a handler) and
        // the summary reads it. output: per-unit uses with handler or method ids, a module graph per field, bounded and skippable
        var host = _f.Manifest("BeneficiaryHostForm").SharedState!;
        var id = Assert.Single(host, x => x.Id == "state:WizardSession.BeneficiaryId");
        Assert.Equal(("SampleLegacy.Session.WizardSession", "BeneficiaryId", "int"), (id.Type, id.Member, id.ValueType));
        Assert.Empty(id.Reads);
        Assert.Equal(new[] { StoreChoice }, id.Writes);                                           // one call below the handler
        Assert.Equal(new[] { StoreChoice }, Assert.Single(host, x => x.Id == "state:WizardSession.Changes").Writes);   // ++ is a write
        Assert.Equal(new[] { "handler:BeneficiaryHostForm/btnChoose_Click" }, Assert.Single(host, x => x.Id == "state:WizardSession.PolicyId").Reads);
        Assert.DoesNotContain(host, x => x.Member == "Current");                                // the static accessor is the way in, not state
        var cover = _f.Manifest("CoverSummaryForm").SharedState!;
        Assert.Equal(new[] { "handler:CoverSummaryForm/btnConfirm_Click" }, Assert.Single(cover, x => x.Id == "state:WizardSession.BeneficiaryId").Reads);
        Assert.Equal(new[] { "handler:CoverSummaryForm/ApplyDefaults" }, Assert.Single(cover, x => x.Id == "state:WizardSession.BeneficiaryName").Reads);
        Assert.Null(_f.Manifest("PolicyWizardStep3").SharedState);                             // touches none

        var graph = _f.Module().SharedState!;
        Assert.Equal(new[] { "SampleLegacy.Session.*" }, graph.Types);
        Assert.Equal(new[] { "state:WizardSession.BeneficiaryId", "state:WizardSession.BeneficiaryName", "state:WizardSession.Changes", "state:WizardSession.PolicyId" }, graph.Fields.Select(x => x.Id));
        var field = Assert.Single(graph.Fields, x => x.Id == "state:WizardSession.BeneficiaryId");
        Assert.Equal((1, "int", (bool?)null), (field.Version, field.ValueType, field.Retired));
        Assert.Equal(new[] { new StateEnd("form:BeneficiaryHostForm", StoreChoice) }, field.Producers);
        Assert.Equal(new[] { new StateEnd("form:CoverSummaryForm", "handler:CoverSummaryForm/btnConfirm_Click") }, field.Consumers);
        Assert.Empty(Assert.Single(graph.Fields, x => x.Id == "state:WizardSession.PolicyId").Producers);   // read, never written in the module
        Assert.Matches(@"shared-state: \d+ bod(y|ies) to depth 2, \d+ access\(es\), \d+\.\ds", _f.Log);

        // bounded: depth 0 records only what handlers touch; an empty sharedState skips the pass
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "state-depth0");
        cfg.SharedStateDepth = 0;
        Extractor.Run(cfg, "M", "BeneficiaryHostForm", new StringWriter());
        FormManifest Read(string output, string form) => JsonSerializer.Deserialize<FormManifest>(File.ReadAllText(Path.Combine(output, "M", "manifests", form + ".json")), Json.Options)!;
        Assert.Equal(new[] { "state:WizardSession.PolicyId" }, Read(cfg.Output, "BeneficiaryHostForm").SharedState!.Select(x => x.Id));
        cfg.SharedState.Clear();
        cfg.Output = Path.Combine(_f.OutRoot, "state-off");
        var log = new StringWriter();
        Extractor.Run(cfg, "M", "BeneficiaryHostForm", log);
        Assert.Contains("shared-state: skipped (no sharedState types configured)", log.ToString());
        Assert.Null(Read(cfg.Output, "BeneficiaryHostForm").SharedState);
        Assert.Null(JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(Path.Combine(cfg.Output, "M", "module.json")), Json.Options)!.SharedState);

        // versioned: a member whose value type changed gets the next version; one nothing touches any more is kept, retired
        cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "state-version");
        Extractor.Run(cfg, "M", "BeneficiaryHostForm,CoverSummaryForm", new StringWriter());
        var modulePath = Path.Combine(cfg.Output, "M", "module.json");
        var before = JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(modulePath), Json.Options)!;
        var edited = before.SharedState!.Fields.Select(x => x.Id == "state:WizardSession.BeneficiaryId" ? x with { ValueType = "string" } : x)
            .Append(new SharedStateField("state:WizardSession.Gone", 3, "SampleLegacy.Session.WizardSession", "int", new(), new())).ToList();
        File.WriteAllText(modulePath, JsonSerializer.Serialize(before with { SharedState = before.SharedState with { Fields = edited } }, Json.Options));
        Extractor.Run(cfg, "M", "CoverSummaryForm", new StringWriter());
        var after = JsonSerializer.Deserialize<ModuleIndex>(File.ReadAllText(modulePath), Json.Options)!.SharedState!.Fields;
        Assert.Equal((2, "int"), (Assert.Single(after, x => x.Id == "state:WizardSession.BeneficiaryId").Version, Assert.Single(after, x => x.Id == "state:WizardSession.BeneficiaryId").ValueType));
        var gone = Assert.Single(after, x => x.Id == "state:WizardSession.Gone");
        Assert.Equal((3, (bool?)true), (gone.Version, gone.Retired));
        Assert.Equal(1, Assert.Single(after, x => x.Id == "state:WizardSession.BeneficiaryName").Version);
    }

    [Fact]
    public void issue_17_c_composition_records_which_unit_embeds_which_with_its_setup_and_the_factory_variants_each_host_gets()
    {
        // input: BeneficiaryHostForm opens BeneficiarySearchForm after Setup(...), CoverSummaryForm after a reflection call, and
        // ClientSearchForm through ClientSearchFormFactory.Create(TenantCode). output: embed: entries, not dyn:, and a module graph
        var host = _f.Manifest("BeneficiaryHostForm");
        Assert.Empty(host.DynamicUi);                                                              // a form the host opens is not dynamic UI
        var embeds = host.Embeds!;
        Assert.Equal(new[] { "embed:BeneficiaryHostForm/btnChoose_Click/search", "embed:BeneficiaryHostForm/btnFindClient_Click/finder", "embed:BeneficiaryHostForm/btnSummary_Click/summary" }, embeds.Select(x => x.Id));
        var search = embeds[0];
        Assert.Equal(("form:BeneficiarySearchForm", "SampleLegacy.Forms.BeneficiarySearchForm", "handler:BeneficiaryHostForm/btnChoose_Click", "dialog"), (search.Unit, search.Class, search.Via, search.Mode));
        Assert.Null(search.Factory);
        var setup = Assert.Single(search.Calls);
        Assert.Equal("Setup", setup.Member);
        Assert.Equal(new[] { "WizardSession.Current.PolicyId", "AllowDeceased", "null" }, setup.Args);
        Assert.Equal(new Loc("Forms/BeneficiaryHostForm.cs", 27, 27), setup.Loc);
        Assert.Equal(new[] { "Selected" }, search.Reads.Select(x => x.Member));
        Assert.Equal("var search = new BeneficiarySearchForm();", search.Statement);
        var summary = embeds[2];
        var reflected = Assert.Single(summary.Calls);
        Assert.Equal(("ApplyDefaults", (bool?)true), (reflected.Member, reflected.Reflection));
        var finder = embeds[1];
        Assert.Equal(("form:ClientSearchForm", Create, "dialog"), (finder.Unit, finder.Factory, finder.Mode));

        var comp = _f.Module().Composition!;
        Assert.Equal(new[] { "form:BeneficiarySearchForm", "form:ClientSearchForm", "form:CoverSummaryForm", "form:PayeeLookupPanel" }, comp.Select(x => x.Unit));   // the last is #18's
        var bs = Assert.Single(Assert.Single(comp, x => x.Unit == "form:BeneficiarySearchForm").Hosts);
        Assert.Equal(("form:BeneficiaryHostForm", "embed:BeneficiaryHostForm/btnChoose_Click/search", "dialog"), (bs.Unit, bs.Embed, bs.Mode));
        Assert.Equal(new[] { "Setup(WizardSession.Current.PolicyId, AllowDeceased, null)" }, bs.Setup);
        Assert.Null(bs.Variants);
        var cs = Assert.Single(Assert.Single(comp, x => x.Unit == "form:ClientSearchForm").Hosts);
        Assert.Equal(Create, cs.Factory);
        Assert.Equal(new[] { "Create(TenantCode)" }, cs.Setup);
        Assert.Equal(new[] { "LRK -> Larkspur", "THS -> Thistle", "THS-NORTH -> Thistle", "default -> base" }, cs.Variants);   // #20's cases: how this host varies
        Assert.Equal(new[] { "ApplyDefaults() (reflection)" }, Assert.Single(Assert.Single(comp, x => x.Unit == "form:CoverSummaryForm").Hosts).Setup);
    }

    [Fact]
    public void issue_17_d_dependency_catalog_aggregates_svc_ids_with_request_reply_templates_errors_and_fixture_provenance()
    {
        // input: the module's service calls, the example registry/dependencies.json and fixtures (one recorded with its environment,
        // one synthetic). output: module.json dependencies with shapes and callers; the catalog adds errors and provenance
        var deps = _f.Module().Dependencies!;
        Assert.Equal(_f.Module().ServiceCalls, deps.Select(x => x.Id));
        var add = Assert.Single(deps, x => x.Id == "svc:IPolicyService.AddBeneficiary");
        Assert.Equal(("SampleLegacy.Services.IPolicyService", "AddBeneficiary", "void AddBeneficiary(int policyId, int clientId)"), (add.Type, add.Method, add.Signature));
        Assert.Equal("{\"policyId\":\"int\",\"clientId\":\"int\"}", add.Request!.ToJsonString());
        Assert.Null(add.Reply);
        var caller = Assert.Single(add.Callers);
        Assert.Equal(("form:CoverSummaryForm", "handler:CoverSummaryForm/btnConfirm_Click"), (caller.Unit, caller.Via));
        Assert.Equal(new[] { "session.PolicyId", "session.BeneficiaryId" }, caller.Args);
        var search = Assert.Single(deps, x => x.Id == "svc:IClientService.SearchClients");
        Assert.Equal("{\"criteria\":{\"Surname\":\"string\",\"Postcode\":\"string\",\"DateOfBirth\":\"System.DateTime?\",\"IncludeDeceased\":\"bool\",\"MaxResults\":\"int\"}}", search.Request!.ToJsonString());
        Assert.Equal("[{\"ClientId\":\"int\",\"Surname\":\"string\",\"Forename\":\"string\",\"DateOfBirth\":\"System.DateTime\",\"Postcode\":\"string\"}]", search.Reply!.ToJsonString());
        Assert.Equal(new[] { "form:BeneficiarySearchForm", "form:ClaimClientSearchForm", "form:ClientSearchForm" }, search.Callers.Select(x => x.Unit).Distinct());

        var dir = _f.ModuleCopy("17d");
        Assert.Equal("training host (invented)", Fixtures.Read(Path.Combine(dir, "fixtures", "add-beneficiary.json")).Environment);
        var catalog = Registry.Catalog(dir);
        Assert.Equal(deps.Select(x => x.Id), catalog.Select(x => x.Id));
        var entry = Assert.Single(catalog, x => x.Id == "svc:IPolicyService.AddBeneficiary");
        Assert.Equal(add.Signature, entry.Signature);
        var error = Assert.Single(entry.Errors);
        Assert.Equal(("duplicate", "add-beneficiary.duplicate"), (error.Case, error.Fixture));
        Assert.Equal(new[] { ("add-beneficiary", "recorded", "2026-10-20", "training host (invented)"), ("add-beneficiary.duplicate", "synthetic", (string?)null, (string?)null) },
            entry.Fixtures.Select(x => (x.Name, x.Provenance, x.Date, x.Environment)));
        var md = Registry.RenderCatalog(catalog);
        Assert.Contains("## svc:IPolicyService.AddBeneficiary", md);
        Assert.Contains("- add-beneficiary: recorded 2026-10-20, training host (invented)", md);
        Assert.Contains("- add-beneficiary.duplicate: synthetic, shape verified", md);
        Assert.Contains("- duplicate: error BENEFICIARY_EXISTS", md);

        // an error case with neither a fixture nor a citation is a claim nothing backs
        var path = Path.Combine(dir, "registry", "dependencies.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(", \"fixture\": \"add-beneficiary.duplicate\"", ""));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "REGISTRY-UNCITED" && x.Message.Contains("svc:IPolicyService.AddBeneficiary error 'duplicate'"));
    }

    [Fact]
    public void issue_17_e_scenario_registry_is_queryable_and_lint_flags_an_unreferenced_or_unverified_scenario()
    {
        // input: the example scenarios: scn:beneficiary-chosen, which both fixtures name, and scn:deceased-beneficiary-refused, which
        // nothing references and nobody verified. output: query answers, then warnings that the gate turns into errors
        var dir = _f.ModuleCopy("17e");
        IEnumerable<string> Q(ScenarioQuery q) => Registry.Query(dir, q).Select(x => x.Id);
        Assert.Equal(new[] { "scn:beneficiary-chosen" }, Q(new ScenarioQuery(Touches: "svc:IPolicyService.AddBeneficiary")));
        Assert.Equal(new[] { "scn:beneficiary-chosen" }, Q(new ScenarioQuery(Touches: "state:WizardSession.BeneficiaryId")));
        Assert.Equal(new[] { "scn:beneficiary-chosen", "scn:deceased-beneficiary-refused" }, Q(new ScenarioQuery(Touches: "handler:BeneficiaryHostForm/btnChoose_Click")));
        Assert.Equal(new[] { "scn:deceased-beneficiary-refused" }, Q(new ScenarioQuery(Negative: true)));
        Assert.Equal(new[] { "scn:beneficiary-chosen", "scn:deceased-beneficiary-refused" }, Q(new ScenarioQuery(Role: "agent", Right: "policy.edit")));
        Assert.Empty(Q(new ScenarioQuery(Role: "supervisor")));
        Assert.Equal(new[] { "scn:deceased-beneficiary-refused" }, Q(new ScenarioQuery(Unverified: true)));
        var chosen = Registry.Query(dir, new ScenarioQuery(Touches: "svc:IPolicyService.AddBeneficiary")).Single();
        Assert.Equal(new[] { "add-beneficiary", "add-beneficiary.duplicate" }, chosen.Fixtures);         // derived from each fixture's scenario
        Assert.Equal(("2026-10-20", 1), (chosen.LastVerified, chosen.Version));

        var findings = Registry.Check(dir, gate: false);
        Assert.Equal(new[] { "SCENARIO-UNREFERENCED", "SCENARIO-UNVERIFIED" }, findings.Select(x => x.Code).OrderBy(x => x));
        Assert.All(findings, x => Assert.Equal("warning", x.Severity));
        Assert.All(findings, x => Assert.Contains("scn:deceased-beneficiary-refused", x.Message));
        Assert.All(Registry.Check(dir, gate: true), x => Assert.Equal("error", x.Severity));

        // the map stop hook, with the real binary: the two scenario warnings are listed and do not block; a stale stamp does
        var root = Path.GetDirectoryName(Path.GetDirectoryName(dir))!;
        if (!OperatingSystem.IsWindows() && GuardHookTests.Sh() != null && Git(root, "init", "-q"))
        {
            Directory.CreateDirectory(Path.Combine(root, "legacy", "hooks"));
            File.Copy(Path.Combine(_f.RepoRoot, "adopt", "hooks", "lint-on-stop.sh"), Path.Combine(root, "legacy", "hooks", "lint-on-stop.sh"), overwrite: true);
            File.WriteAllText(Path.Combine(root, "legacy", ".phase"), "map\n");
            var wrapper = Path.Combine(Path.GetDirectoryName(root)!, "parity-17e.sh");
            File.WriteAllText(wrapper, $"#!/bin/sh\nexec '{Environment.ProcessPath}' '{Path.Combine(AppContext.BaseDirectory, "parity.dll")}' \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.True(Git(root, "add", "-A") && Git(root, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "extract"));
            var scn = Path.Combine(dir, "registry", "scenarios.json");
            var reviewed = File.ReadAllText(scn);
            (int Code, string Err) Stop()
            {
                var psi = new ProcessStartInfo(GuardHookTests.Sh()!, "legacy/hooks/lint-on-stop.sh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
                psi.Environment["PARITY_BIN"] = wrapper;
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var err = p.StandardError.ReadToEnd(); p.WaitForExit();
                return (p.ExitCode, err);
            }
            File.WriteAllText(scn, reviewed + "\n");                                                   // map touched the registry
            var warned = Stop();
            Assert.True(warned.Code == 0, $"map: scenario warnings must not block: {warned.Err}");
            Assert.Contains("SCENARIO-UNVERIFIED", warned.Err);
            Assert.Contains("SCENARIO-UNREFERENCED", warned.Err);
            File.WriteAllText(scn, reviewed.Replace(Registry.Revision(dir), "sha256:0000000000000000"));
            var stale = Stop();
            Assert.Equal(2, stale.Code);
            Assert.Contains("REGISTRY-STALE", stale.Err);
            File.WriteAllText(scn, reviewed);
        }

        // a rule references a scenario by id and version; an unknown id or a version the registry moved past is a lint error
        var spec = Path.Combine(dir, "spec", "BeneficiarySearchForm.md");
        Directory.CreateDirectory(Path.GetDirectoryName(spec)!);
        var hash = ManifestHash.Of(Path.Combine(dir, "manifests", "BeneficiarySearchForm.json"));
        List<Finding> Lint(string scenarios)
        {
            File.WriteAllText(spec, $"---\nform: form:BeneficiarySearchForm\nmanifest: {hash}\nstatus: draft\n---\n# x\n## Fields\n## Rules\n### R-BeneficiarySearchForm-001\n- kind: data\n- covers: evt:BeneficiarySearchForm/$this.Setup\n- cite: handler:BeneficiarySearchForm/Setup\n- scenarios: {scenarios}\n- given: the host passes allowDeceased false\n- when: Setup runs\n- then: the search leaves deceased clients out\n## No behavior\n## Service calls\n## Open questions\n- none\n");
            return SpecLinter.Lint(spec, gate: false, implementDir: null);
        }
        Assert.DoesNotContain(Lint("scn:deceased-beneficiary-refused@1"), x => x.Code.StartsWith("SCENARIO", StringComparison.Ordinal));
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Code == "SCENARIO-UNREFERENCED");
        Assert.Contains(Lint("scn:no-such-scenario"), x => x.Code == "SCENARIO-UNKNOWN" && x.Severity == "error");
        Assert.Contains(Lint("scn:deceased-beneficiary-refused@2"), x => x.Code == "SCENARIO-VERSION" && x.Message.Contains("registry has v1"));

        // the schema holds only invented or structural fields: any other key is refused, so no real data can ride along
        var path = Path.Combine(dir, "registry", "scenarios.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"role\": \"agent\",", "\"role\": \"agent\", \"policyNumber\": \"P-1\","));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "REGISTRY-INVALID" && x.Message.Contains("'policyNumber'"));
        File.WriteAllText(path, File.ReadAllText(path).Replace(", \"policyNumber\": \"P-1\"", "").Replace("\"role\": \"agent\"", "\"role\": \"Agent Smith\""));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "REGISTRY-INVALID" && x.Message.Contains("role"));
        // a fixture that names a scenario the registry lacks
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Agent Smith\"", "\"agent\"").Replace("scn:beneficiary-chosen", "scn:beneficiary-picked"));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "SCENARIO-UNKNOWN" && x.Message.Contains("add-beneficiary"));
    }

    [Fact]
    public void issue_17_f_accumulation_a_re_extract_reports_what_changed_and_the_registry_is_stale_until_reviewed_and_stamped()
    {
        // input: CoverSummaryForm extracted, the registry initialized, a spec bound; then re-extracts that change the module.
        // output: the log names the changed manifest and the now-stale spec; registry check is STALE (and GAP for a new svc:)
        // until the registry is brought up to date and stamped; human answers live as decisions that close questions
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Output = Path.Combine(_f.OutRoot, "accumulate-17");
        var dir = Path.Combine(cfg.Output, "M");
        Extractor.Run(cfg, "M", "CoverSummaryForm", new StringWriter());
        var created = Registry.Init(dir);
        Assert.Equal(new[] { "registry/decisions.json", "registry/dependencies.json", "registry/scenarios.json" }, created);
        Assert.Contains("\"id\": \"svc:IPolicyService.AddBeneficiary\"", File.ReadAllText(Path.Combine(dir, "registry", "dependencies.json")));
        Assert.Empty(Registry.Check(dir, gate: true));
        Assert.Throws<InvalidOperationException>(() => Registry.Init(dir));                    // never overwrites a registry

        // decisions: owner, date and a citation; a question closed by one; a quirk dropped by one
        File.Copy(Path.Combine(_f.RepoRoot, "examples", "PolicyWizard", "registry", "decisions.json"), Path.Combine(dir, "registry", "decisions.json"), overwrite: true);
        var spec = Path.Combine(dir, "spec", "CoverSummaryForm.md");
        Directory.CreateDirectory(Path.GetDirectoryName(spec)!);
        var hash = ManifestHash.Of(Path.Combine(dir, "manifests", "CoverSummaryForm.json"));
        List<Finding> Lint(string decision, string closed) {
            File.WriteAllText(spec, $"---\nform: form:CoverSummaryForm\nmanifest: {hash}\nstatus: draft\n---\n# x\n## Fields\n## Rules\n### R-CoverSummaryForm-001\n- kind: action\n- covers: evt:CoverSummaryForm/btnConfirm.Click\n- cite: handler:CoverSummaryForm/btnConfirm_Click\n- given: no beneficiary chosen\n- when: Confirm is clicked\n- then: a warning shows and nothing is sent\n- quirk: the warning text is fixed, decided-by {decision}\n## No behavior\n## Service calls\n## Open questions\n{closed}- none\n");
            return SpecLinter.Lint(spec, gate: true, implementDir: null);
        }
        var ok = Lint("dec:summary-needs-a-beneficiary", "- closed by decision dec:summary-needs-a-beneficiary: refuse without a beneficiary? - yes\n");
        Assert.DoesNotContain(ok, x => x.Code is "OPEN" or "DROP-REASON" or "DECISION-UNKNOWN");
        var bad = Lint("dec:nobody-decided", "- closed by decision dec:nobody-decided: refuse without a beneficiary? - yes\n");
        Assert.Equal(2, bad.Count(x => x.Code == "DECISION-UNKNOWN"));
        Assert.Contains(bad, x => x.Code == "OPEN");
        var decisions = Path.Combine(dir, "registry", "decisions.json");
        File.WriteAllText(decisions, File.ReadAllText(decisions).Replace("\"owner\": \"example-reviewer\",", ""));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "DECISION-INVALID" && x.Message.Contains("owner"));
        File.Copy(Path.Combine(_f.RepoRoot, "examples", "PolicyWizard", "registry", "decisions.json"), decisions, overwrite: true);
        Lint("dec:summary-needs-a-beneficiary", "");

        // a re-extract that changes the manifest: the log says what changed and which spec went stale
        cfg.FormBaseTypes.Add("LegacyApp.Framework.NoSuchBaseForm");
        var log = new StringWriter();
        Extractor.Run(cfg, "M", "CoverSummaryForm", log);
        var text = log.ToString().Replace('\\', '/');
        Assert.Contains("changed: CoverSummaryForm  1 added, 0 removed, 0 changed  (parity diff manifests/CoverSummaryForm.prev.json manifests/CoverSummaryForm.json)", text);
        Assert.Contains("stale spec: spec/CoverSummaryForm.md  (parity rebind spec/CoverSummaryForm.md)", text);
        Assert.Contains("registry: STALE, review registry/ against this extract (parity registry check), then parity registry stamp", text);
        var stale = Assert.Single(Registry.Check(dir, gate: false));
        Assert.Equal(("REGISTRY-STALE", "error"), (stale.Code, stale.Severity));
        Assert.Contains(SpecLinter.Lint(spec, gate: true, implementDir: null), x => x.Code == "STALE");
        Rebind.Run(spec, null);
        Assert.Contains(SpecLinter.Lint(spec, gate: true, implementDir: null), x => x.Code == "REGISTRY-STALE");   // the implement gate waits for the registry too
        Registry.Stamp(dir);
        Assert.Empty(Registry.Check(dir, gate: true));
        Assert.DoesNotContain(SpecLinter.Lint(spec, gate: true, implementDir: null), x => x.Code == "REGISTRY-STALE");

        // a re-extract that brings a new svc: id: a gap the stamp refuses to paper over
        log = new StringWriter();
        Extractor.Run(cfg, "M", "BeneficiarySearchForm", log);
        Assert.DoesNotContain("changed: ", log.ToString());                                     // a new manifest is not a change
        var codes = Registry.Check(dir, gate: true).Select(x => x.Code).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "REGISTRY-GAP", "REGISTRY-STALE" }, codes);
        var refused = Assert.Throws<InvalidOperationException>(() => Registry.Stamp(dir));
        Assert.Contains("REGISTRY-GAP", refused.Message);
        Assert.Contains("svc:IClientService.SearchClients", refused.Message);
        var deps = Path.Combine(dir, "registry", "dependencies.json");
        File.WriteAllText(deps, File.ReadAllText(deps).Replace("\"dependencies\": [", "\"dependencies\": [\n    { \"id\": \"svc:IClientService.SearchClients\", \"errors\": [] },"));
        Registry.Stamp(dir);
        Assert.Empty(Registry.Check(dir, gate: true));
    }

    [Fact]
    public void issue_17_g_map_phase_reads_the_legacy_tree_and_writes_only_the_registries_with_citations_that_resolve()
    {
        // input: the guard and stop hooks in a temp repo with .phase map; evidence recorded in map; a registry citing it.
        // output: the source and the module readable, writes outside registry/ and evidence/ refused; specify and implement
        // still refuse the source; the stop hook gates the registry in map and refuses other changes
        var phases = Kit.Load().Manifest.Phases;                                                   // init installs the skill, doctor accepts .phase map
        Assert.Equal(new[] { "extract", "map", "specify" }, phases.Select(x => x.Name).Take(3));
        Assert.Equal(("parity-map", true), (phases[1].Skill, phases[1].Driven));
        Assert.Contains("adopt/.claude/skills/parity-map/SKILL.md", Kit.Load().Files.Keys);

        // evidence may be recorded in map, and a registry claim may cite it
        var dir = _f.ModuleCopy("17g");
        var cfg = ExtractConfig.Load(Path.Combine(_f.RepoRoot, "fixtures", "extract.config.json"));
        cfg.Project = cfg.Resolve(cfg.Project);
        cfg.Output = Path.GetDirectoryName(dir)!;
        cfg.BaseDir = cfg.Output;                                                                  // the layout's legacy/: config and .phase side by side
        File.WriteAllText(Path.Combine(cfg.BaseDir, ".phase"), "map\n");
        var ev = Evidence.AddFromConfig(cfg, "PolicyWizard", "BeneficiarySearchForm", "Forms/BeneficiarySearchForm.cs:39-39", "the search passes the host's allowDeceased on");
        var scenarios = Path.Combine(dir, "registry", "scenarios.json");
        File.WriteAllText(scenarios, File.ReadAllText(scenarios).Replace("\"cite\": [\"handler:BeneficiarySearchForm/Setup\"]", $"\"cite\": [\"handler:BeneficiarySearchForm/Setup\", \"{ev.Id}\"]"));
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Code.StartsWith("CITE", StringComparison.Ordinal) || x.Code.StartsWith("EV", StringComparison.Ordinal));
        File.WriteAllText(scenarios, File.ReadAllText(scenarios).Replace(ev.Id, "ev:BeneficiarySearchForm/9"));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "CITE-UNKNOWN" && x.Message.Contains("ev:BeneficiarySearchForm/9"));
        File.WriteAllText(scenarios, File.ReadAllText(scenarios).Replace("\"cite\": [\"handler:BeneficiarySearchForm/Setup\", \"ev:BeneficiarySearchForm/9\"]", "\"cite\": []"));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "REGISTRY-UNCITED" && x.Message.Contains("scn:deceased-beneficiary-refused"));

        if (GuardHookTests.Sh() == null) return; // no POSIX shell: the hooks cannot run here at all
        var baseDir = Path.Combine(Path.GetTempPath(), "parity-map-" + Guid.NewGuid().ToString("N")[..8]);
        var work = Path.Combine(baseDir, "repo");
        var legacy = Path.Combine(baseDir, "Legacy", "AcmeAdmin");
        Directory.CreateDirectory(Path.Combine(work, "legacy", "hooks"));
        Directory.CreateDirectory(Path.Combine(work, "legacy", "M", "registry"));
        Directory.CreateDirectory(Path.Combine(legacy, "Forms"));
        foreach (var hook in new[] { "guard-legacy.sh", "lint-on-stop.sh" })
            File.Copy(Path.Combine(_f.RepoRoot, "adopt", "hooks", hook), Path.Combine(work, "legacy", "hooks", hook));
        File.WriteAllText(Path.Combine(work, "legacy", "extract.config.json"), "{\n  \"project\": \"" + legacy.Replace('\\', '/') + "/AcmeAdmin.csproj\",\n  \"output\": \".\"\n}\n");
        var phase = Path.Combine(work, "legacy", ".phase");
        var form = Path.Combine(legacy, "Forms", "X.cs");
        try
        {
            File.WriteAllText(phase, "map\n");
            foreach (var (tool, input) in new (string, object)[]
            {
                ("Read", new { file_path = form }),
                ("Grep", new { pattern = "Session", path = legacy }),
                ("Bash", new { command = "grep -rn WizardSession " + legacy.Replace('\\', '/') }),
                ("Read", new { file_path = "legacy/M/manifests/X.md" }),
                ("Write", new { file_path = "legacy/M/registry/scenarios.json", content = "{}" }),
                ("Edit", new { file_path = Path.Combine(work, "legacy", "M", "evidence", "X.json"), old_string = "a", new_string = "b" }),
            })
            {
                var r = GuardHookTests.Run(work, HookCall(tool, input, work), hideJq: false);
                Assert.True(r.Code == 0, $"map: {tool} {JsonSerializer.Serialize(input)} should pass: {r.Err}");
            }
            foreach (var (tool, input) in new (string, object)[]
            {
                ("Write", new { file_path = "legacy/M/spec/X.md", content = "x" }),
                ("Edit", new { file_path = "legacy/M/manifests/X.json", old_string = "a", new_string = "b" }),
                ("Write", new { file_path = "src/app.ts", content = "x" }),
                ("Edit", new { file_path = form, old_string = "a", new_string = "b" }),
                ("MultiEdit", new { file_path = "legacy/M/registry/../spec/X.md", edits = new object[0] }),
                ("NotebookEdit", new { notebook_path = "x.ipynb", new_source = "x" }),
            })
            {
                var r = GuardHookTests.Run(work, HookCall(tool, input, work), hideJq: false);
                Assert.True(r.Code == 2, $"map: {tool} {JsonSerializer.Serialize(input)} should be refused");
                Assert.Contains("map phase writes only legacy/<module>/registry/ and legacy/<module>/evidence/", r.Err);
            }
            // specify and implement are as before: the source stays unreadable
            foreach (var p in new[] { "specify", "implement" })
            {
                File.WriteAllText(phase, p + "\n");
                Assert.Equal(2, GuardHookTests.Run(work, HookCall("Read", new { file_path = form }, work), hideJq: false).Code);
                Assert.Equal(0, GuardHookTests.Run(work, HookCall("Write", new { file_path = "legacy/M/spec/X.md", content = "x" }, work), hideJq: false).Code);
            }

            // the stop hook: in map it gates the registry and refuses changes outside it; a stub parity records how it was called
            if (!Git(work, "init", "-q")) return;                                                    // no git: the stop hook cannot see changes
            var calls = Path.Combine(baseDir, "calls.txt");
            var stub = Path.Combine(baseDir, "parity-stub.sh");
            File.WriteAllText(stub, "#!/bin/sh\necho \"$*\" >> '" + calls.Replace('\\', '/') + "'\n[ -f '" + Path.Combine(baseDir, "red").Replace('\\', '/') + "' ] && { echo 'REGISTRY-STALE'; exit 1; }\nexit 0\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            (int Code, string Err) Stop()
            {
                var psi = new ProcessStartInfo(GuardHookTests.Sh()!, "legacy/hooks/lint-on-stop.sh") { WorkingDirectory = work, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
                psi.Environment["PARITY_BIN"] = stub;
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var err = p.StandardError.ReadToEnd(); p.WaitForExit();
                return (p.ExitCode, err);
            }
            File.WriteAllText(phase, "map\n");
            File.WriteAllText(Path.Combine(work, "legacy", "M", "registry", "scenarios.json"), "{}");
            Assert.Equal(0, Stop().Code);
            Assert.Contains("registry check legacy/M", File.ReadAllText(calls));
            Assert.DoesNotContain("--gate", File.ReadAllText(calls));
            File.WriteAllText(Path.Combine(baseDir, "red"), "");
            var red = Stop();
            Assert.Equal(2, red.Code);
            Assert.Contains("REGISTRY-STALE", red.Err);
            File.Delete(Path.Combine(baseDir, "red"));
            Directory.CreateDirectory(Path.Combine(work, "src"));
            File.WriteAllText(Path.Combine(work, "src", "app.ts"), "x");
            var outside = Stop();
            Assert.Equal(2, outside.Code);
            Assert.Contains("map phase changed files outside legacy/<module>/registry/ and evidence/: src/app.ts", outside.Err);
            // extract: the hook reads state, not the diff; a stale registry hands off to map and never blocks
            File.Delete(Path.Combine(work, "src", "app.ts"));
            Directory.CreateDirectory(Path.Combine(work, "legacy", "M", "manifests"));
            File.WriteAllText(Path.Combine(work, "legacy", "M", "manifests", "X.json"), "{}");
            Assert.True(Git(work, "add", "-A") && Git(work, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "-q", "-m", "extract"));
            File.WriteAllText(phase, "extract\n");
            File.WriteAllText(calls, "");
            var clean = Stop();
            Assert.Equal(0, clean.Code);
            Assert.Contains("registry check legacy/M", File.ReadAllText(calls));
            Assert.DoesNotContain("map phase", clean.Err);
            File.WriteAllText(Path.Combine(baseDir, "red"), "");
            var handoff = Stop();
            Assert.Equal(0, handoff.Code);
            Assert.Contains("legacy/M is STALE", handoff.Err);
            Assert.Contains("run the map phase next", handoff.Err);
            foreach (var p in new[] { "map", "specify", "implement" })
            {
                File.WriteAllText(phase, p + "\n");
                var blocked = Stop();
                Assert.True(blocked.Code == 2, $"{p}: a stale registry blocks the stop on a clean tree");
                Assert.Contains("REGISTRY-STALE", blocked.Err);
            }
            File.Delete(Path.Combine(baseDir, "red"));
            File.WriteAllText(phase, "specify\n");
            Assert.Equal(0, Stop().Code);
        }
        finally { Directory.Delete(baseDir, true); }
    }

    private static bool Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
