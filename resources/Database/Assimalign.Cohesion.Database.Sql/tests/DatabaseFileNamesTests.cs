using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The area's one database-name rule and name-to-files mapping (#1392), compiled into the SQL
/// assembly from the root's shared source as into every model. The mapping is a pure function, so
/// it is tested here without an engine: every spelling of a name maps to one directory, unequal
/// names to different ones, and a catalog file set is never a database's directory. The rule is a
/// fixed character set, so these tests state it as literals and never consult
/// <see cref="Path.GetInvalidFileNameChars"/>, which differs between Windows and Linux: the same
/// assertions hold on every operating system CI runs.
/// </summary>
public sealed class DatabaseFileNamesTests
{
    // The characters the rule allows, written out: the test's own statement of the rule.
    private const string allowedValue = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-.";

    private static readonly string _root = Path.Combine(Path.GetTempPath(), "cohesion-names-root");

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Database names: every spelling of a name maps to the one lower-case directory (#1392)")]
    [InlineData("sales")]
    [InlineData("SALES")]
    [InlineData("Sales")]
    [InlineData("sAlEs")]
    public void GetDirectory_CaseVariant_ShouldMapToTheLowerCaseDirectory(string name)
    {
        // Act
        string directory = DatabaseFileNames.GetDirectory(_root, name);

        // Assert
        directory.ShouldBe(Path.Combine(_root, "sales"));
        DatabaseFileNames.GetStorageName(name).ShouldBe("sales");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Database names: the mapping ignores the current culture, so a Turkish culture still lowers 'I' to 'i' (#1392)")]
    public void GetStorageName_UnderATurkishCulture_ShouldLowerInvariantly()
    {
        // Arrange: tr-TR lowers 'I' to the dotless 'ı' (U+0131), which DatabaseName's ordinal
        // comparison would never call equal to 'i': a culture-sensitive mapping would give
        // 'INVOICES' a directory no other spelling reaches.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            "I".ToLower(CultureInfo.CurrentCulture).ShouldBe("ı");

            // Act
            string storageName = DatabaseFileNames.GetStorageName("INVOICES");
            string directory = DatabaseFileNames.GetDirectory(_root, "Invoices");

            // Assert
            storageName.ShouldBe("invoices");
            directory.ShouldBe(Path.Combine(_root, "invoices"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Database names: names that differ otherwise than by case map to different directories (#1392)")]
    public void GetDirectory_UnequalNames_ShouldMapToDifferentDirectories()
    {
        // Arrange: names one character, one dot or one suffix apart, and the old catalog spelling.
        string[] names = ["sales", "sales.catalog", "sales_", "sales-", "sale", "sales1", "s.ales", "catalog", "sql", "sales.dat", "SALES.CATALOG2"];

        // Act
        var directories = names.Select(name => DatabaseFileNames.GetDirectory(_root, name)).ToArray();

        // Assert
        directories.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
        directories.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(names.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Database names: two names share a directory exactly when DatabaseName calls them equal (#1392)")]
    public void GetStorageName_AnyTwoValidNames_ShouldBeEqualExactlyWhenTheDatabaseNamesAre()
    {
        // Arrange: every allowed character, in a name of its own and beside each case of a letter.
        var names = new List<string>();
        foreach (char character in allowedValue.Where(character => character != '.'))
        {
            names.Add(character.ToString());
            names.Add("a" + character);
            names.Add("A" + character);
            names.Add("a." + character);
        }

        // Act and assert: the mapping and DatabaseName's equality are the same relation.
        foreach (string left in names)
        {
            foreach (string right in names)
            {
                bool sameDatabase = new DatabaseName(left) == new DatabaseName(right);
                bool sameDirectory = string.Equals(DatabaseFileNames.GetStorageName(left), DatabaseFileNames.GetStorageName(right), StringComparison.Ordinal);
                sameDirectory.ShouldBe(sameDatabase, $"'{left}' and '{right}'");
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Database names: a catalog file set is inside its database's directory and is never another database's directory (#1392)")]
    public void GetFiles_CatalogFileSet_ShouldNeverBeADatabaseDirectory()
    {
        // Arrange: until #1392 the catalog of 'sales' was the directory 'sales.catalog'.
        string[] names = ["sales", "sales.catalog", "catalog", "SALES.catalog", "orders"];

        foreach (string name in names)
        {
            // Act
            string directory = DatabaseFileNames.GetDirectory(_root, name);
            var catalog = DatabaseFileNames.GetFiles(directory, DatabaseFileNames.CatalogFileSet);
            var data = DatabaseFileNames.GetFiles(directory, SqlStorageStrategy.DataFileSet);

            // Assert: both sets are files of the database's own directory.
            foreach (string file in new[] { catalog.Data, catalog.Journal, catalog.Backup, data.Data, data.Journal, data.Backup })
            {
                Path.GetDirectoryName(file).ShouldBe(directory);
                foreach (string other in names)
                {
                    string.Equals(file, DatabaseFileNames.GetDirectory(_root, other), StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
                }
            }

            catalog.Data.ShouldBe(Path.Combine(directory, "catalog.dat"));
            data.Data.ShouldBe(Path.Combine(directory, "sql.dat"));
        }

        DatabaseFileNames.GetDirectory(_root, "sales.catalog").ShouldNotBe(DatabaseFileNames.GetDirectory(_root, "sales"));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Database names: the rule accepts the same names on every operating system (#1392)")]
    [InlineData("a")]
    [InlineData("Z")]
    [InlineData("0")]
    [InlineData("sales")]
    [InlineData("Sales_2026")]
    [InlineData("sales-eu")]
    [InlineData("sales.catalog")]
    [InlineData("a.b.c")]
    [InlineData("_private")]
    [InlineData("-dash")]
    [InlineData("dash-")]
    [InlineData("console")]
    [InlineData("com10")]
    [InlineData("lpt")]
    [InlineData("auxiliary")]
    [InlineData("nul_")]
    [InlineData("conx.db")]
    [InlineData("catalog")]
    public void GetRefusal_ValidName_ShouldAccept(string name)
    {
        // Act
        string? refusal = DatabaseFileNames.GetRefusal(name);

        // Assert
        refusal.ShouldBeNull();
        Should.NotThrow(() => DatabaseFileNames.ThrowIfInvalid(name));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Database names: the rule refuses the same names on every operating system (#1392)")]
    [InlineData("a b", "U+0020 at position 1")]
    [InlineData("a/b", "'/' at position 1")]
    [InlineData("a\\b", "'\\' at position 1")]
    [InlineData("a:b", "':' at position 1")]
    [InlineData("a*b", "'*' at position 1")]
    [InlineData("a?b", "'?' at position 1")]
    [InlineData("a\"b", "'\"' at position 1")]
    [InlineData("a<b", "'<' at position 1")]
    [InlineData("a>b", "'>' at position 1")]
    [InlineData("a|b", "'|' at position 1")]
    [InlineData("a\0b", "U+0000 at position 1")]
    [InlineData("a\tb", "U+0009 at position 1")]
    [InlineData("salés", "U+00E9 at position 3")]
    [InlineData("ſales", "U+017F at position 0")]
    [InlineData("Kelvin", "U+212A at position 0")]
    [InlineData("sales+eu", "'+' at position 5")]
    [InlineData(".", "starts with a dot")]
    [InlineData("..", "starts with a dot")]
    [InlineData(".sales", "starts with a dot")]
    [InlineData("sales.", "ends with a dot")]
    [InlineData("a..b", "two dots in a row")]
    [InlineData("CON", "'CON' is a Windows device name")]
    [InlineData("con", "'con' is a Windows device name")]
    [InlineData("Con.txt", "'Con' is a Windows device name")]
    [InlineData("nul.db", "'nul' is a Windows device name")]
    [InlineData("PRN", "'PRN' is a Windows device name")]
    [InlineData("aux.data.log", "'aux' is a Windows device name")]
    [InlineData("COM0", "'COM0' is a Windows device name")]
    [InlineData("com1", "'com1' is a Windows device name")]
    [InlineData("LPT9", "'LPT9' is a Windows device name")]
    [InlineData("lpt1.log", "'lpt1' is a Windows device name")]
    public void GetRefusal_InvalidName_ShouldRefuseWithTheReason(string name, string reason)
    {
        // Act
        string? refusal = DatabaseFileNames.GetRefusal(name);
        var thrown = Should.Throw<ArgumentException>(() => DatabaseFileNames.ThrowIfInvalid(name));

        // Assert: the rule, then the reason, the same on every model.
        refusal.ShouldNotBeNull().ShouldContain(reason, Case.Sensitive);
        thrown.ParamName.ShouldBe("name");
        thrown.Message.ShouldStartWith(DatabaseFileNames.Rule, Case.Sensitive);
        thrown.Message.ShouldContain(reason, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Database names: the rule allows 1 to 128 characters (#1392)")]
    public void GetRefusal_Length_ShouldAllowOneToOneHundredTwentyEightCharacters()
    {
        // Arrange
        string longest = new('a', DatabaseFileNames.MaximumLength);
        string tooLong = longest + "a";

        // Act and assert
        DatabaseFileNames.MaximumLength.ShouldBe(128);
        DatabaseFileNames.GetRefusal(longest).ShouldBeNull();
        DatabaseFileNames.GetRefusal(tooLong).ShouldBe("The name is 129 characters long.");
        DatabaseFileNames.GetRefusal("").ShouldBe("The name is empty.");
        DatabaseFileNames.GetRefusal(null).ShouldBe("The name is empty.");
        Should.Throw<ArgumentNullException>(() => DatabaseFileNames.ThrowIfInvalid(null!));
        Should.Throw<ArgumentException>(() => DatabaseFileNames.ThrowIfInvalid("   ")).ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Database names: inside a name the rule allows exactly ASCII letters, digits, '_', '-' and '.', none of them a character any operating system refuses (#1392)")]
    public void GetRefusal_EveryCharacter_ShouldAllowExactlyTheFixedSet()
    {
        // Arrange: the characters Windows refuses in a file name, and those Linux and macOS refuse,
        // written out rather than read from the running platform.
        const string windowsRefuses = "<>:\"/\\|?*";
        const string unixRefuses = "/\0";

        // Act: every UTF-16 code unit, between two letters.
        var allowed = new List<char>();
        for (int code = char.MinValue; code <= char.MaxValue; code++)
        {
            char character = (char)code;
            if (DatabaseFileNames.GetRefusal("a" + character + "a") is null)
            {
                allowed.Add(character);
            }
        }

        // Assert
        new string(allowed.ToArray()).ShouldBe(new string(allowedValue.Order().ToArray()));
        allowed.ShouldAllBe(character => character >= ' ' && windowsRefuses.IndexOf(character) < 0 && unixRefuses.IndexOf(character) < 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Database names: an opened database reports the spelling its storage header kept, when it names the database opened (#1392)")]
    [InlineData("SALES", "sales", "sales")]
    [InlineData("sales", "Sales", "Sales")]
    [InlineData("Sales", "Sales", "Sales")]
    [InlineData("archive", "sales", "archive")]
    [InlineData("Archive", "sales", "archive")]
    [InlineData("Archive", "", "archive")]
    [InlineData("Archive", "a/b", "archive")]
    public void GetOpenedName_StoredSpelling_ShouldReportItWhenItNamesTheDatabase(string requested, string stored, string expected)
    {
        // Act
        string reported = DatabaseFileNames.GetOpenedName(requested, stored);

        // Assert: never the caller's spelling unless it is the stored one.
        reported.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Database names: only a lower-case directory that follows the rule is a database's directory (#1392)")]
    [InlineData("sales", true)]
    [InlineData("sales.catalog", true)]
    [InlineData("a-b_c.0", true)]
    [InlineData("Sales", false)]
    [InlineData("SALES", false)]
    [InlineData("a b", false)]
    [InlineData("con", false)]
    [InlineData(".hidden", false)]
    public void IsStorageName_DirectoryName_ShouldBeTrueOnlyForAStorageName(string directoryName, bool expected)
    {
        // Act and assert
        DatabaseFileNames.IsStorageName(directoryName).ShouldBe(expected);
    }
}
