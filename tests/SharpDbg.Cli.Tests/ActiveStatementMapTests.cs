using AwesomeAssertions;
using SharpDbg.Infrastructure.Debugger;
using Point = SharpDbg.Infrastructure.Debugger.ModuleMetadataReader.SequencePointInfo;

namespace SharpDbg.Cli.Tests;

/// <summary>
/// Where an active frame may continue in a new method version (<see cref="ManagedDebugger.MapActiveStatementOffset"/>).
/// A wrong answer skips or repeats code, so every case that cannot be shown to be the same statement must refuse.
/// </summary>
public class ActiveStatementMapTests
{
	private const string Doc = "/src/Program.cs";

	// Old: statements on lines 10, 11, 12 (the one on 11 spans two lines in the multi-line case).
	private static readonly Point[] Old =
	[
		new(0, Doc, 10, 9, 10, 30, false),
		new(5, Doc, 11, 9, 11, 25, false),
		new(9, "", 0xFEEFEE, 0, 0xFEEFEE, 0, true),
		new(12, Doc, 12, 9, 12, 40, false),
	];

	// New: two lines inserted above, so the same statements start on 12, 13, 14; IL moved too.
	private static readonly Point[] New =
	[
		new(0, Doc, 12, 9, 12, 30, false),
		new(7, Doc, 13, 9, 13, 25, false),
		new(14, Doc, 14, 9, 14, 40, false),
	];

	private static SourceLineMap Map(params UnchangedLineRange[] ranges) => new(Doc, ranges);

	[Fact]
	public void UnchangedStatement_MapsToItsShiftedSequencePoint()
		=> ManagedDebugger.MapActiveStatementOffset(Old, New, [Map(new UnchangedLineRange(10, 12, 3))], 5).Should().Be(7);

	[Fact]
	public void EditedStatementLine_IsNotRemapped()
		// Line 11 is outside every unchanged range: its text changed.
		=> ManagedDebugger.MapActiveStatementOffset(Old, New, [Map(new UnchangedLineRange(10, 12, 1), new UnchangedLineRange(12, 14, 1))], 5).Should().BeNull();

	[Fact]
	public void StatementSpanningAnEditedLine_IsNotRemapped()
	{
		Point[] old = [new(5, Doc, 11, 9, 12, 5, false)];
		Point[] @new = [new(7, Doc, 13, 9, 14, 5, false)];
		// Line 11 kept, line 12 edited: the statement as a whole is not the same.
		ManagedDebugger.MapActiveStatementOffset(old, @new, [Map(new UnchangedLineRange(10, 12, 2))], 5).Should().BeNull();
		ManagedDebugger.MapActiveStatementOffset(old, @new, [Map(new UnchangedLineRange(10, 12, 3))], 5).Should().Be(7);
	}

	[Fact]
	public void OffsetInsideAStatement_IsNotRemapped()
		=> ManagedDebugger.MapActiveStatementOffset(Old, New, [Map(new UnchangedLineRange(10, 12, 3))], 6).Should().BeNull();

	[Fact]
	public void HiddenSequencePoint_IsNotRemapped()
		=> ManagedDebugger.MapActiveStatementOffset(Old, New, [Map(new UnchangedLineRange(1, 1, 100))], 9).Should().BeNull();

	[Fact]
	public void ColumnsThatMoved_AreNotTheSameStatement()
	{
		Point[] @new = [new(7, Doc, 13, 13, 13, 29, false)];
		ManagedDebugger.MapActiveStatementOffset(Old, @new, [Map(new UnchangedLineRange(10, 12, 3))], 5).Should().BeNull();
	}

	[Fact]
	public void DocumentWithoutALineMap_IsNotRemapped()
		=> ManagedDebugger.MapActiveStatementOffset(Old, New, [new SourceLineMap("/src/Other.cs", [new UnchangedLineRange(10, 12, 3)])], 5).Should().BeNull();
}
