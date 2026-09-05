using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

/// <summary>
/// Phase 5 Task 1 — Model2Vec relocation under <c>SCRINIA_HOME</c>.
///
/// <para>The built-in embedding model must resolve through Phase 2's <see cref="UserHome.Resolve"/>
/// resolver to <c>SCRINIA_HOME/models/m2v-MiniLM-L6-v2</c>, never to the executable-relative
/// <c>AppContext.BaseDirectory/models</c> path the pre-fix setup used. These tests drive the real
/// <see cref="ScriniaCommands.Setup"/> entry point against an isolated <c>SCRINIA_HOME</c> and an
/// isolated workspace root.</para>
///
/// <para>Setup is invoked with <c>noOllama: true</c> and the resolved model directory is pre-seeded
/// with both model files, so the fixed path performs no network download. The pre-fix
/// executable-relative path (<c>AppContext.BaseDirectory/models/m2v-MiniLM-L6-v2</c>) is trapped with
/// a FILE so the old <c>Directory.CreateDirectory</c> fails fast — deterministically red, with no
/// network dependency and no model download — while the fixed path never touches it.</para>
///
/// <para><c>Setup</c> mutates <see cref="WorkspaceSetup"/>'s process-wide static workspace root via
/// <c>Configure</c>, so this class lives in the global workspace-config collection that serializes
/// such mutations.</para>
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class ModelRelocationTests : IDisposable
{
    private const string ScriniaHomeVar = "SCRINIA_HOME";

    private readonly string _home;
    private readonly string _homeModelsDir;
    private readonly string _workspace;
    private readonly string _appContextModelDir;

    private string? _previousHome;
    private bool _trapCreated;

    public ModelRelocationTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"scrinia_reloc_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);

        // Pre-seed the SCRINIA_HOME model location so the fixed setup finds it and downloads nothing.
        _homeModelsDir = Path.Combine(_home, "models", "m2v-MiniLM-L6-v2");
        Directory.CreateDirectory(_homeModelsDir);
        File.WriteAllText(Path.Combine(_homeModelsDir, "model.safetensors"), "model2vec model.safetensors");
        File.WriteAllText(Path.Combine(_homeModelsDir, "vocab.txt"), "model2vec vocab");

        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_reloc_ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);

        // Phase 2 resolver honors an isolated, non-empty absolute SCRINIA_HOME exactly.
        _previousHome = Environment.GetEnvironmentVariable(ScriniaHomeVar);
        Environment.SetEnvironmentVariable(ScriniaHomeVar, _home);

        // Trap the OLD executable-relative model path with a FILE so the pre-fix code's
        // Directory.CreateDirectory(AppContext/models/m2v-MiniLM-L6-v2) throws immediately —
        // failing fast with no network download — while the fix resolves to SCRINIA_HOME and
        // never touches that path.
        _appContextModelDir = Path.Combine(AppContext.BaseDirectory, "models", "m2v-MiniLM-L6-v2");
        string appContextModelsParent = Path.GetDirectoryName(_appContextModelDir)!;
        if (File.Exists(appContextModelsParent))
            File.Delete(appContextModelsParent);
        Directory.CreateDirectory(appContextModelsParent);
        if (!File.Exists(_appContextModelDir))
        {
            File.WriteAllText(_appContextModelDir, "model-relocation-trap");
            _trapCreated = true;
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ScriniaHomeVar, _previousHome);

        // Remove the trap file/dir and any executable-relative model the pre-fix code may have
        // downloaded there, leaving the test bin folder clean.
        try
        {
            if (Directory.Exists(_appContextModelDir)) Directory.Delete(_appContextModelDir, recursive: true);
            else if (File.Exists(_appContextModelDir)) File.Delete(_appContextModelDir);
        }
        catch { }

        try { Directory.Delete(_home, recursive: true); } catch { }
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public void UserHome_ResolvesIsolatedHome_ExactAbsoluteOverride()
    {
        // The isolated, non-empty absolute SCRINIA_HOME override is honored exactly.
        UserHome.Resolve().Should().Be(_home);

        // And the model path is built entirely from that resolved home.
        Path.Combine(UserHome.Resolve(), "models", "m2v-MiniLM-L6-v2")
            .Should().Be(_homeModelsDir);
    }

    [Fact]
    public async Task Setup_ResolvesModel2VecUnderScriniaHome_NeverAppBaseDirectory()
    {
        int exit = await new ScriniaCommands().Setup(workspaceRoot: _workspace, noOllama: true);

        exit.Should().Be(0, "scri setup with the built-in model already present completes cleanly");

        // The model resolves through the Phase 2 resolver to SCRINIA_HOME — never the executable
        // directory — and the pre-seeded built-in files are the ones setup confirmed present.
        Path.Combine(UserHome.Resolve(), "models", "m2v-MiniLM-L6-v2")
            .Should().Be(_homeModelsDir);
        File.Exists(Path.Combine(_homeModelsDir, "model.safetensors")).Should().BeTrue();
        File.Exists(Path.Combine(_homeModelsDir, "vocab.txt")).Should().BeTrue();

        // The pre-fix code resolved under AppContext.BaseDirectory/models; the fix must never create
        // or populate that executable-relative path.
        Directory.Exists(_appContextModelDir).Should().BeFalse(
            "Model2Vec must resolve under SCRINIA_HOME, never AppContext.BaseDirectory/models");
    }

}
