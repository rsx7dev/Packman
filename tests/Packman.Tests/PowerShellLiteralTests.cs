using System.Management.Automation.Language;
using Packman.Helpers;
using Xunit;

namespace Packman.Tests;

/// <summary>The real Helpers/PowerShellLiteral, checked by parsing its output with the PowerShell parser.</summary>
public sealed class PowerShellLiteralTests
{
    public static TheoryData<string> Hostile => new()
    {
        "O'Reilly",
        "'; Remove-Item x; '",
        "$(whoami)",
        "$env:PATH",
        "back`tick`n",
        "\u2018curly single\u2019 \u201aand\u201b",
        "\u201ccurly double\u201d \u201eand\u201c",
        "\"; Remove-Item x; \"",
        "line one\nline two",
    };

    [Fact]
    public void SingleQuotedDoublesApostrophes() => Assert.Equal("O''Reilly", PowerShellLiteral.SingleQuoted("O'Reilly"));

    [Fact]
    public void SingleQuotedDoublesTypographicQuotes()
        => Assert.Equal("it\u2019\u2019s \u2018\u2018x", PowerShellLiteral.SingleQuoted("it\u2019s \u2018x"));

    [Fact]
    public void NullIsEmpty()
    {
        Assert.Equal("", PowerShellLiteral.SingleQuoted(null));
        Assert.Equal("", PowerShellLiteral.DoubleQuoted(null));
    }

    [Fact]
    public void DoubleQuotedEscapesBacktickQuoteAndDollar()
        => Assert.Equal("a``b`\"c`$d`\u201ce`\u201df", PowerShellLiteral.DoubleQuoted("a`b\"c$d\u201ce\u201df"));

    [Theory]
    [MemberData(nameof(Hostile))]
    public void SingleQuotedParsesBackToTheInput(string value)
        => AssertOneConstant("'" + PowerShellLiteral.SingleQuoted(value) + "'", value);

    [Theory]
    [MemberData(nameof(Hostile))]
    public void DoubleQuotedParsesBackToTheInput(string value)
        => AssertOneConstant("\"" + PowerShellLiteral.DoubleQuoted(value) + "\"", value);

    private static void AssertOneConstant(string script, string expected)
    {
        var ast = Parser.ParseInput(script, out _, out var errors);

        Assert.Empty(errors);
        var statement = Assert.Single(ast.EndBlock.Statements);
        var pipeline = Assert.IsType<PipelineAst>(statement);
        var expression = Assert.IsType<CommandExpressionAst>(Assert.Single(pipeline.PipelineElements)).Expression;
        var literal = Assert.IsType<StringConstantExpressionAst>(expression);
        Assert.Equal(expected, literal.Value);
    }
}
