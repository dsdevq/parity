namespace Parity.Model;

/// <summary>Where a manifest item lives in the legacy source. The citation target.</summary>
public sealed record Loc(string File, int Start, int End);

public sealed record ControlInfo(
    string Id,
    string Field,
    string Type,
    string Category,
    string? Parent,
    string? LayoutItem,
    string? Caption,
    SortedDictionary<string, string> Props,
    List<string> Bindings,
    SortedDictionary<string, string> Events,
    Loc? Loc);

public sealed record EventWiring(string Id, string Control, string Event, string Handler, string Source, Loc Loc);

public sealed record BindingInfo(string Id, string Control, string Property, string DataSource, string Member, string Source, Loc Loc);

public sealed record ValidationRuleInfo(string Id, string Control, string Rule, string Source, Loc Loc);

public sealed record HandlerInfo(
    string Id,
    string Name,
    string Kind,
    List<string> Events,
    List<string> Reads,
    List<string> Writes,
    List<string> Calls,
    List<string> ServiceCalls,
    List<string> Unresolved,
    Loc Loc,
    string Source);

public sealed record MethodInfo(
    string Id,
    string Display,
    int Depth,
    List<string> Reads,
    List<string> Writes,
    List<string> Calls,
    List<string> ServiceCalls,
    List<string> Unresolved,
    Loc? Loc,
    string? Source,
    string? Assembly);

/// <summary>
/// A call across the service boundary. Signature, Request and Reply describe the call's shape for the module's dependency
/// catalog: Request maps each parameter to its type (a model type's public properties one level down), Reply likewise for
/// the return type (null for void). Sites pairs each caller with the arguments it passes.
/// </summary>
public sealed record ServiceCallInfo(string Id, string Type, string Method, List<string> CalledFrom, List<string> Args,
    string? Signature = null, System.Text.Json.Nodes.JsonNode? Request = null, System.Text.Json.Nodes.JsonNode? Reply = null, List<CallSite>? Sites = null);

/// <summary>One call of a service: the handler or method id that makes it and the argument expressions, as written.</summary>
public sealed record CallSite(string Via, List<string> Args);

public sealed record DynamicUiInfo(string Id, string Type, string? AddedTo, string Handler, Loc Loc, string Statement);

/// <summary>
/// A static fact a slice or a bound model reads by name: a const or static readonly literal, or a resource text.
/// Not a citable id; rules keep citing the handler or method that uses it.
/// </summary>
public sealed record ConstantInfo(
    string Kind,
    string Type,
    string Member,
    string ValueType,
    string? Value,
    Loc? Declared,
    List<string> Cultures,
    Loc? FirstUse,
    List<string> UsedBy);

public sealed record RawStatement(string Statement, Loc Loc);

public sealed record Diagnostic(string Code, string Message, Loc? Loc);

public sealed record FormManifest(
    string SchemaVersion,
    string Id,
    string ClassName,
    string Namespace,
    List<string> Bases,
    List<string> Files,
    string? Caption,
    List<ControlInfo> Controls,
    List<EventWiring> Events,
    List<BindingInfo> Bindings,
    List<ValidationRuleInfo> ValidationRules,
    List<HandlerInfo> Handlers,
    List<MethodInfo> Methods,
    List<ServiceCallInfo> ServiceCalls,
    List<DynamicUiInfo> DynamicUi,
    List<ConstantInfo> Constants,
    List<RawStatement> Raw,
    List<Diagnostic> Diagnostics,
    List<VariantInfo>? Variants = null,
    List<FactoryInfo>? Factories = null,
    UnitContract? Contract = null,
    List<SharedStateUse>? SharedState = null,
    List<EmbedInfo>? Embeds = null);

/// <summary>
/// A way into the unit from outside it. Kind: ctor, entry (a non-private method nothing in the unit wires), or reflection (a
/// private member some file names in a GetMethod/GetProperty/InvokeMember string; InvokedFrom lists where).
/// </summary>
public sealed record ContractEntry(string Handler, string Kind, string Signature, Loc Loc, List<string>? InvokedFrom = null);

/// <summary>
/// One value crossing the unit's edge. Inputs: param (of an entry), property (a public setter the unit reads), state (a
/// shared-state member read), host (a member read on an object handed in through a ctor or entry parameter). Outputs:
/// property (a public getter the unit writes), dialog-result, state (a shared-state member written), host (a member written
/// on a handed-in object), host-call (a method called on one). Via lists the handler and method ids that read or write it.
/// </summary>
public sealed record ContractValue(string Kind, string Name, string? Type, List<string> Via, List<string>? Values = null);

/// <summary>The unit's Inputs/Outputs contract, derived from its handlers, methods and declarations.</summary>
public sealed record UnitContract(List<ContractEntry> Entries, List<ContractValue> Inputs, List<ContractValue> Outputs);

/// <summary>
/// A member of a configured shared-state type the unit reads or writes, in its handlers and in methods down to
/// sharedStateDepth. Id is state:&lt;Type&gt;.&lt;Member&gt;; Reads and Writes list the handler and method ids.
/// </summary>
public sealed record SharedStateUse(string Id, string Type, string Member, string ValueType, List<string> Reads, List<string> Writes);

/// <summary>One use of an embedded unit by its host: a call (Reflection when made through GetMethod(...).Invoke), a property set or read.</summary>
public sealed record EmbedUse(string Member, List<string> Args, Loc Loc, bool? Reflection = null);

/// <summary>
/// Another unit of the module this unit creates (embed:&lt;Host&gt;/&lt;owner&gt;/&lt;local&gt;). Mode: dialog (ShowDialog), show,
/// embedded (added to a container, then also a dyn: entry) or created. Factory is the method id when the instance comes from
/// a factory rather than new, with the arguments the host passes it; the factory's cases (#20) then say which variant the host gets.
/// </summary>
public sealed record EmbedInfo(string Id, string Unit, string Class, string Via, string Mode, string? Factory,
    List<EmbedUse> Calls, List<EmbedUse> Sets, List<EmbedUse> Reads, Loc Loc, string Statement, List<string>? FactoryArgs = null);

/// <summary>One property a variant sets differently from the base unit. Base is null when the base never sets it.</summary>
public sealed record VariantChange(string Property, string? Base, string? Value, Loc? Loc, string? Control = null);

/// <summary>
/// One difference between a variant and its base unit, keyed var:&lt;Variant&gt;/&lt;kind&gt;/&lt;name&gt;.
/// Kinds: added, removed, retagged (controls); event (a wiring the variant adds); override, handler, ctor (code);
/// service-added, service-removed (the service boundary); constant (a constant or resource text the variant reads that the
/// base does not read as such). Target names what the entry changes in the base.
/// </summary>
public sealed record VariantDelta(
    string Id,
    string Kind,
    string? Target,
    string? Detail,
    string? Type,
    string? Category,
    List<VariantChange>? Changes,
    List<string>? Events,
    List<string>? Reads,
    List<string>? Writes,
    List<string>? Calls,
    List<string>? ServiceCalls,
    string? Source,
    Loc? Loc);

/// <summary>A tenant or customer variant of the unit: a subclass, possibly in another project, described as a delta on the base.</summary>
public sealed record VariantInfo(
    string Id,
    string Name,
    string? Project,
    string Class,
    List<string> Bases,
    List<string> Files,
    List<VariantDelta> Delta,
    List<VariantChange> Layout,
    List<MethodInfo> Methods,
    List<RawStatement> Raw,
    List<Diagnostic> Diagnostics);

/// <summary>Which class a factory hands out for which key. Variant is "base" for the unit itself, the variant name, or null for another class.</summary>
public sealed record FactoryCase(string Key, string Class, string? Variant, Loc Loc);

public sealed record FactoryInfo(string Id, string On, List<FactoryCase> Cases, Loc Loc);

public sealed record ModuleIndex(
    string SchemaVersion,
    string Module,
    string Project,
    List<string> Forms,
    List<string> ServiceCalls,
    List<DuplicateCluster> Duplicates,
    SharedStateGraph? SharedState = null,
    List<CompositionEntry>? Composition = null,
    List<DependencyEntry>? Dependencies = null);

/// <summary>One unit's end of a shared-state field: the unit and the handler or method id that reads or writes it.</summary>
public sealed record StateEnd(string Unit, string Via);

/// <summary>
/// A shared-state member across the module. Version starts at 1 and goes up when the member's value type changes; a field no
/// manifest touches any more stays, Retired, so its id and version are never reused for something else.
/// </summary>
public sealed record SharedStateField(string Id, int Version, string Type, string ValueType, List<StateEnd> Producers, List<StateEnd> Consumers, bool? Retired = null);

public sealed record SharedStateGraph(List<string> Types, List<SharedStateField> Fields);

/// <summary>A host of a unit: how it creates and sets it up. Variants are the factory's cases for this unit (#20), when it comes from one.</summary>
public sealed record CompositionHost(string Unit, string Embed, string Mode, string? Factory, List<string>? Variants, List<string> Setup);

public sealed record CompositionEntry(string Unit, List<CompositionHost> Hosts);

public sealed record DependencyCaller(string Unit, string Via, List<string> Args);

/// <summary>A svc: id across the module, with its shape and every caller; error cases and fixtures live in the registry and fixtures/.</summary>
public sealed record DependencyEntry(string Id, string Type, string Method, string? Signature,
    System.Text.Json.Nodes.JsonNode? Request, System.Text.Json.Nodes.JsonNode? Reply, List<DependencyCaller> Callers);

public sealed record DuplicateCluster(string Hash, string Kind, int Tokens, List<string> Members);
