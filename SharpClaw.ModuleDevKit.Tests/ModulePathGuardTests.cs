using NUnit.Framework;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.ModuleDevKit.Tests;

[TestFixture]
public sealed class ModulePathGuardTests
{
    [Test]
    public void EnsureContainedIn_ReturnsCanonicalPath_WhenPathStaysUnderParent()
    {
        var parent = CreateTempDirectory();
        var nested = Path.Combine(parent, "module", "module.json");

        var actual = ModulePathGuard.EnsureContainedIn(nested, parent);

        Assert.That(actual, Is.EqualTo(Path.GetFullPath(nested)));
    }

    [Test]
    public void EnsureContainedIn_RejectsParentTraversal()
    {
        var parent = CreateTempDirectory();
        var escaped = Path.Combine(parent, "..", "outside.json");

        Assert.Throws<InvalidOperationException>(() =>
            ModulePathGuard.EnsureContainedIn(escaped, parent));
    }

    [Test]
    public void EnsureContainedIn_RejectsSiblingWithSharedPrefix()
    {
        var root = CreateTempDirectory();
        var parent = Path.Combine(root, "external-modules");
        var sibling = Path.Combine(root, "external-modules2", "module.json");

        Directory.CreateDirectory(parent);

        Assert.Throws<InvalidOperationException>(() =>
            ModulePathGuard.EnsureContainedIn(sibling, parent));
    }

    [TestCase("Module.cs")]
    [TestCase("module.json")]
    public void EnsureFileName_ReturnsSimpleFileNames(string fileName)
    {
        Assert.That(ModulePathGuard.EnsureFileName(fileName), Is.EqualTo(fileName));
    }

    [TestCase("")]
    [TestCase("..")]
    [TestCase("module..json")]
    [TestCase("nested/module.json")]
    [TestCase(@"nested\module.json")]
    public void EnsureFileName_RejectsTraversalAndPathSegments(string fileName)
    {
        Assert.Throws<ArgumentException>(() => ModulePathGuard.EnsureFileName(fileName));
    }

    [Test]
    public void EnsureFileName_RejectsNullBytes()
    {
        Assert.Throws<ArgumentException>(() => ModulePathGuard.EnsureFileName("module\0.json"));
    }

    [TestCase("Example.csproj")]
    [TestCase("Example.CSPROJ")]
    public void EnsureExtension_AllowsExpectedExtensionCaseInsensitively(string fileName)
    {
        Assert.That(ModulePathGuard.EnsureExtension(fileName, ".csproj"), Is.EqualTo(fileName));
    }

    [Test]
    public void EnsureExtension_RejectsUnexpectedExtension()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ModulePathGuard.EnsureExtension("Example.txt", ".csproj"));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
