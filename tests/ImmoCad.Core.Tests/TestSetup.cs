using System.Runtime.CompilerServices;
using ImmoCad.Core;
using Xunit;

// Language is process-wide state: tests run one at a time, and the legacy assertions are written against the French texts,
// so the suite starts in French. Dedicated tests (LanguageTests) cover the English default and runtime switching, and restore it.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]   // one collection = tests run one at a time

namespace ImmoCad.Core.Tests;

public static class TestSetup
{
    [ModuleInitializer] public static void Init() => Translations.Language = "fr";
}
