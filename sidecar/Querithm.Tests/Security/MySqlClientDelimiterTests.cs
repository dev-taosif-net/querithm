using System.Diagnostics;
using Querithm.Intelligence.MySql;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 2, code audit 2026-11-25 (C2): a client <c>DELIMITER $$</c> keeps a stored program's body whole, and the batch
/// goes to the server as one text. The server knows no DELIMITER: it ends a statement at a semicolon outside a compound
/// body, so <c>SELECT 1; DROP TABLE t $$</c> ran a DROP the guard read as part of a SELECT. Each statement the server will
/// see is classified now; stored-program bodies (which run later) stay quiet.
/// </summary>
public class MySqlClientDelimiterTests
{
    static readonly MySqlLanguageService L = new();

    [Theory]
    [InlineData("DELIMITER $$\nSELECT 1; DROP TABLE t $$\nDELIMITER ;", "drop")]
    [InlineData("DELIMITER //\nSELECT 1; UPDATE t SET x = 1 //", "updateWithoutWhere")]
    // The WHERE of a later statement is not the DELETE's.
    [InlineData("DELIMITER $$\nDELETE FROM t; SELECT 1 FROM u WHERE a = 1 $$", "deleteWithoutWhere")]
    [InlineData("DELIMITER $$\nCREATE PROCEDURE p() BEGIN SELECT 1; END; DROP TABLE t $$", "drop")]
    // A body without BEGIN … END is one statement: the semicolon ends the CREATE.
    [InlineData("DELIMITER $$\nCREATE PROCEDURE p() SELECT 1; DROP TABLE t $$", "drop")]
    [InlineData("DELIMITER $$\nCREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW SET NEW.x = 1; DROP TABLE t $$", "drop")]
    // An empty block closes at its END, whatever follows.
    [InlineData("DELIMITER $$\nCREATE PROCEDURE p() BEGIN END; DROP TABLE x; SELECT 1; END $$", "drop")]
    [InlineData("DELIMITER $$\nSELECT CASE WHEN a THEN IF(b, 1, 2) END FROM t; DROP TABLE t $$", "drop")]
    [InlineData("DELIMITER $$\nSELECT begin FROM t; DROP TABLE t $$", "drop")]
    [InlineData("DELIMITER $$\nBEGIN; DROP TABLE t $$", "drop")]
    // MariaDB's compound statement outside a program runs its body now.
    [InlineData("DELIMITER $$\nBEGIN NOT ATOMIC DROP TABLE t; END $$", "drop")]
    [InlineData("DELIMITER $$\nBEGIN NOT ATOMIC IF 1 > 0 THEN DELETE FROM t; END IF; END $$", "deleteWithoutWhere")]
    [InlineData("DELIMITER $$\nlbl: BEGIN NOT ATOMIC TRUNCATE TABLE t; END lbl $$", "truncate")]
    public void A_statement_the_server_runs_after_a_semicolon_is_classified(string text, string rule)
    {
        var c = L.Classify(text);
        Assert.Contains(c.Destructive, f => f.Rule == rule);
        // Still one batch: the client statement is what is sent.
        Assert.Single(c.Batches);
    }

    [Theory]
    [InlineData("DELIMITER $$\nCREATE PROCEDURE p() BEGIN DELETE FROM t; UPDATE t SET x = 1; DROP TABLE u; END $$\nDELIMITER ;")]
    [InlineData("""
        DELIMITER $$
        CREATE DEFINER=`root`@`%` PROCEDURE p(IN n INT)
        BEGIN
          DECLARE i INT DEFAULT 0;
          DECLARE CONTINUE HANDLER FOR SQLEXCEPTION BEGIN ROLLBACK; DELETE FROM log; END;
          DECLARE EXIT HANDLER FOR NOT FOUND BEGIN END;
          lbl: LOOP
            IF i > n THEN LEAVE lbl; END IF;
            IF (i = 2) THEN DELETE FROM t; ELSEIF i = 3 THEN UPDATE t SET x = 1; ELSE BEGIN DROP TABLE u; END; END IF;
            SET i = i + 1;
          END LOOP lbl;
          WHILE i > 0 DO SET i = i - 1; END WHILE;
          REPEAT SET i = i + 1; UNTIL i > 3 END REPEAT;
          CASE i WHEN 1 THEN SELECT 1; ELSE SELECT CASE WHEN i > 2 THEN IF(i > 3, 'a', 'b') ELSE REPEAT('x', 2) END; END CASE;
          DELETE FROM t;
        END $$
        DELIMITER ;
        """)]
    [InlineData("DELIMITER $$\nCREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW BEGIN SET NEW.x = 1; DELETE FROM u; END $$")]
    [InlineData("DELIMITER $$\nCREATE EVENT e ON SCHEDULE EVERY 1 DAY DO BEGIN DELETE FROM t; END $$")]
    [InlineData("DELIMITER $$\nCREATE FUNCTION f(a INT) RETURNS INT DETERMINISTIC BEGIN IF a > 0 THEN RETURN IF(a > 1, 1, 0); END IF; RETURN 0; END $$")]
    public void A_stored_program_body_stays_quiet(string text)
    {
        var c = L.Classify(text);
        Assert.Empty(c.Destructive);
        Assert.False(c.BeginsTransaction);
        Assert.False(c.EndsTransaction);
    }

    /// <summary>A COMMIT hidden after a semicolon steps outside an actual plan's transaction like any other.</summary>
    [Fact]
    public void A_transaction_statement_after_a_semicolon_counts()
    {
        Assert.True(L.Classify("DELIMITER $$\nSELECT 1; COMMIT $$").EndsTransaction);
        Assert.True(L.Classify("DELIMITER $$\nSELECT 1; START TRANSACTION $$").BeginsTransaction);
    }

    /// <summary>Code audit 2026-11-25 (L6): the transaction statements a prepared text runs count like its findings.</summary>
    [Fact]
    public void A_prepared_transaction_statement_counts()
    {
        Assert.True(L.Classify("PREPARE s FROM 'COMMIT'; EXECUTE s;").EndsTransaction);
        Assert.True(L.Classify("EXECUTE IMMEDIATE 'START TRANSACTION';").BeginsTransaction);
    }

    /// <summary>mysqldump writes routines and triggers under <c>DELIMITER ;;</c>, a delimiter of two tokens.</summary>
    [Fact]
    public void A_delimiter_of_two_semicolons_ends_each_statement()
    {
        var text = "DELIMITER ;;\nCREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW BEGIN SET NEW.x = 1; END ;;\nDELIMITER ;\nDROP TABLE t;\nSELECT 1;";
        var st = MySqlSplitter.Split(text);
        Assert.Equal(3, st.Count);
        Assert.EndsWith("END", text[st[0].Start..st[0].End]);
        Assert.Equal("DROP TABLE t", text[st[1].Start..st[1].End]);
        Assert.Single(L.Classify(text).Destructive, f => f.Rule == "drop");
    }

    /// <summary>The mysql client reads DELIMITER only at the start of a line.</summary>
    [Fact]
    public void A_delimiter_after_a_statement_on_the_same_line_is_no_command()
    {
        var text = "SELECT 1; DELIMITER $$\nSELECT 2;";
        var st = MySqlSplitter.Split(text);
        Assert.Equal(2, st.Count);
        Assert.StartsWith("DELIMITER $$", text[st[1].Start..st[1].End]);
        Assert.Equal(3, MySqlSplitter.Split("SELECT 1;\n  DELIMITER $$\nSELECT 2 $$\nSELECT 3 $$").Count);
    }

    /// <summary>
    /// Code audit 2026-11-25 (H4): the DELIMITER check cut out and trimmed the rest of the line for every statement, so
    /// statements on one long line were quadratic. 100,000 statements on one line (about 1 MB) now split in well under
    /// the time the quadratic copy needed (it allocated about 50 GB).
    /// </summary>
    [Fact]
    public void Many_statements_on_one_line_split_in_linear_time()
    {
        var text = string.Concat(Enumerable.Repeat("SELECT 1;", 100_000));
        MySqlSplitter.Split("SELECT 1;"); // warm
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        Assert.Equal(100_000, MySqlSplitter.Split(text).Count);
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_000_000_000, $"allocated {allocated:N0} bytes");
    }
}
