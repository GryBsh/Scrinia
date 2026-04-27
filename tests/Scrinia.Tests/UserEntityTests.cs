using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for user-defined entity types loaded from YAML files in .scrinia/entities/.
/// Covers loading, conflict resolution, merged types, creation, and transition validation.
/// </summary>
public sealed class UserEntityTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private static readonly CancellationToken CT = CancellationToken.None;

    public UserEntityTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
    }

    public void Dispose() => _scope.Dispose();

    private string EntitiesDir
    {
        get
        {
            string dir = Path.Combine(_scope.WorkspaceDir, ".scrinia", "entities");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private string ScriniaBaseDir => Path.Combine(_scope.WorkspaceDir, ".scrinia");

    // ── 1. Valid YAML loading ────────────────────────────────────────────────

    [Fact]
    public void UserEntityLoader_ValidYaml_ReturnsType()
    {
        // Arrange — write a spike.yaml with valid entity definition
        string yaml =
            """
            name: spike
            states:
              - draft
              - active
              - archived
            defaultState: draft
            transitions:
              - from: "*"
                to: draft
                required: []
              - from: draft
                to: active
                required:
                  - description
              - from: active
                to: archived
                required:
                  - reason
            """;
        File.WriteAllText(Path.Combine(EntitiesDir, "spike.yaml"), yaml);

        // Act
        var result = UserEntityLoader.LoadUserDefinedTypes(ScriniaBaseDir);

        // Assert
        result.Should().ContainKey("spike",
            "loader should parse spike.yaml and return a type keyed by 'spike'");
        var typeDef = result["spike"];
        typeDef.TypeName.Should().Be("spike");
        typeDef.ValidStates.Should().Contain("draft");
        typeDef.ValidStates.Should().Contain("active");
        typeDef.ValidStates.Should().Contain("archived");
        typeDef.DefaultState.Should().Be("draft");
        typeDef.Transitions.Should().HaveCount(3);
    }

    // ── 2. Conflicting name is skipped ───────────────────────────────────────

    [Fact]
    public void UserEntityLoader_ConflictingName_Skipped()
    {
        // Arrange — write a YAML with name "goal" which conflicts with built-in
        string yaml =
            """
            name: goal
            states:
              - custom-state
            defaultState: custom-state
            transitions: []
            """;
        File.WriteAllText(Path.Combine(EntitiesDir, "goal.yaml"), yaml);

        // Act
        var result = UserEntityLoader.LoadUserDefinedTypes(ScriniaBaseDir);

        // Assert — "goal" should be skipped because it conflicts with built-in
        result.Should().NotContainKey("goal",
            "user-defined type 'goal' should be skipped because it conflicts with a built-in type");
    }

    // ── 3. GetMergedTypes includes user types ────────────────────────────────

    [Fact]
    public void GetMergedTypes_IncludesUserTypes()
    {
        // Arrange — write a valid user entity YAML
        string yaml =
            """
            name: experiment
            states:
              - proposed
              - running
              - concluded
            defaultState: proposed
            transitions:
              - from: "*"
                to: proposed
                required:
                  - description
            """;
        File.WriteAllText(Path.Combine(EntitiesDir, "experiment.yaml"), yaml);

        // Act
        var merged = EntityTypeRegistry.GetMergedTypes(ScriniaBaseDir);

        // Assert — merged should include both built-in types and the user type
        merged.Should().ContainKey("goal", "merged types should include built-in 'goal'");
        merged.Should().ContainKey("concern", "merged types should include built-in 'concern'");
        merged.Should().ContainKey("project", "merged types should include built-in 'project'");
        merged.Should().ContainKey("experiment",
            "merged types should include user-defined 'experiment'");
    }

    // ── 4. User entity create stores entity ──────────────────────────────────

    [Fact]
    public async Task UserEntity_Create_StoresEntity()
    {
        // Arrange — write a user entity YAML and init project
        string yaml =
            """
            name: spike
            states:
              - draft
              - active
            defaultState: draft
            transitions:
              - from: "*"
                to: draft
                required: []
            """;
        File.WriteAllText(Path.Combine(EntitiesDir, "spike.yaml"), yaml);

        await ScriniaProjectTools.ProjectInit("Goals: test user entities", cancellationToken: CT);

        // Act — create a user entity via EntityDispatch
        string result = await ScriniaProjectTools.EntityDispatch("create", "spike",
            description: "Test spike entity",
            cancellationToken: CT);

        // Assert
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success",
            "creating a user entity should succeed");
        parsed.Content.Should().Contain("spike",
            "response should mention the entity type");
        parsed.Content.Should().Contain("draft",
            "response should mention the default state");
    }

    // ── 5. User entity transition validates required params ──────────────────

    [Fact]
    public async Task UserEntity_Transition_ValidatesParams()
    {
        // Arrange — create a user entity type with required transition params
        string yaml =
            """
            name: spike
            states:
              - draft
              - active
            defaultState: draft
            transitions:
              - from: "*"
                to: draft
                required: []
              - from: draft
                to: active
                required:
                  - description
            """;
        File.WriteAllText(Path.Combine(EntitiesDir, "spike.yaml"), yaml);

        await ScriniaProjectTools.ProjectInit("Goals: test user entity transitions", cancellationToken: CT);

        // Act — attempt transition without required 'id' param
        string result = await ScriniaProjectTools.EntityDispatch("transition", "spike",
            to: "active",
            cancellationToken: CT);

        // Assert — should fail because 'id' is required for transitions
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("error",
            "transition without required 'id' parameter should fail");
    }

    // ── 6. User entity loader returns empty when no entities dir ─────────────

    [Fact]
    public void UserEntityLoader_NoEntitiesDir_ReturnsEmpty()
    {
        // Act — load from a base dir that has no entities/ subdirectory
        var result = UserEntityLoader.LoadUserDefinedTypes(ScriniaBaseDir);

        // Assert
        result.Should().BeEmpty(
            "when .scrinia/entities/ does not exist, loader should return empty dictionary");
    }

    // ── 7. GetMergedTypes returns built-ins when no user types ───────────────

    [Fact]
    public void GetMergedTypes_NullBaseDir_ReturnsBuiltInsOnly()
    {
        // Act
        var merged = EntityTypeRegistry.GetMergedTypes(null);

        // Assert — should be exactly the built-in types
        merged.Should().BeSameAs(EntityTypeRegistry.Types,
            "when scriniaBaseDir is null, GetMergedTypes should return the built-in registry directly");
    }
}
