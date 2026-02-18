using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BoltMacro;

public sealed class Storage
{
    private readonly string _dbPath;

    public Storage(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        EnsureSchema();
    }

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        };

        var c = new SqliteConnection(builder.ToString());
        c.Open();

        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();

        return c;
    }

    private void ExecWithRetry(Action action)
    {
        const int tries = 5;
        for (int i = 0; i < tries; i++)
        {
            try { action(); return; }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6)
            {
                Thread.Sleep(50 + i * 100);
            }
        }
        action();
    }

    private void EnsureSchema()
    {
        ExecWithRetry(() =>
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS macros(
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  steps_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS rules(
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  enabled INTEGER NOT NULL,
  macro_id TEXT NULL,
  trigger_json TEXT NOT NULL,
  repeat_json TEXT NOT NULL,
  state_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS runs(
  id TEXT PRIMARY KEY,
  rule_id TEXT NULL,
  macro_id TEXT NULL,
  start_utc TEXT NOT NULL,
  end_utc TEXT NULL,
  status INTEGER NOT NULL,
  error_text TEXT NULL
);

CREATE TABLE IF NOT EXISTS timeline(
  id TEXT PRIMARY KEY,
  utc_time TEXT NOT NULL,
  source INTEGER NOT NULL,
  severity INTEGER NOT NULL,
  message TEXT NOT NULL,
  details_json TEXT NULL
);

CREATE TABLE IF NOT EXISTS rule_scores(
  id TEXT PRIMARY KEY,
  rule_id TEXT NOT NULL,
  utc_time TEXT NOT NULL,
  score REAL NOT NULL,
  explanation TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS rule_feedback(
  id TEXT PRIMARY KEY,
  rule_id TEXT NOT NULL,
  utc_time TEXT NOT NULL,
  feedback_kind INTEGER NOT NULL
);
";
            cmd.ExecuteNonQuery();
        });
    }

    // Macros
    public void UpsertMacro(MacroModel m) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO macros(id,name,created_at,updated_at,steps_json)
VALUES($id,$name,$created,$updated,$steps)
ON CONFLICT(id) DO UPDATE SET
  name=excluded.name,
  updated_at=excluded.updated_at,
  steps_json=excluded.steps_json;
";
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$name", m.Name);
        cmd.Parameters.AddWithValue("$created", m.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", m.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$steps", JsonUtil.ToJson(m.Steps));
        cmd.ExecuteNonQuery();
    });

    public void DeleteMacro(string id) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM macros WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    public List<MacroModel> ListMacros()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,created_at,updated_at,steps_json FROM macros ORDER BY updated_at DESC;";
        using var r = cmd.ExecuteReader();

        var list = new List<MacroModel>();
        while (r.Read())
        {
            var id = r.GetString(0);
            var name = r.GetString(1);
            var created = DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var updated = DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var stepsJson = r.GetString(4);
            var steps = JsonUtil.FromJson<List<MacroStep>>(stepsJson) ?? new List<MacroStep>();

            list.Add(new MacroModel
            {
                Id = id,
                Name = name,
                CreatedAt = created,
                UpdatedAt = updated,
                Steps = steps
            });
        }
        return list;
    }

    public MacroModel? GetMacro(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,created_at,updated_at,steps_json FROM macros WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var steps = JsonUtil.FromJson<List<MacroStep>>(r.GetString(4)) ?? new List<MacroStep>();
        return new MacroModel
        {
            Id = r.GetString(0),
            Name = r.GetString(1),
            CreatedAt = DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Steps = steps
        };
    }

    // Rules
    public void UpsertRule(RuleModel r) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO rules(id,name,enabled,macro_id,trigger_json,repeat_json,state_json)
VALUES($id,$name,$enabled,$macro,$trigger,$repeat,$state)
ON CONFLICT(id) DO UPDATE SET
  name=excluded.name,
  enabled=excluded.enabled,
  macro_id=excluded.macro_id,
  trigger_json=excluded.trigger_json,
  repeat_json=excluded.repeat_json,
  state_json=excluded.state_json;
";
        cmd.Parameters.AddWithValue("$id", r.Id);
        cmd.Parameters.AddWithValue("$name", r.Name);
        cmd.Parameters.AddWithValue("$enabled", r.Enabled ? 1 : 0);
        if (r.MacroId is null) cmd.Parameters.AddWithValue("$macro", DBNull.Value);
        else cmd.Parameters.AddWithValue("$macro", r.MacroId);
        cmd.Parameters.AddWithValue("$trigger", JsonUtil.ToJson(r.Trigger));
        cmd.Parameters.AddWithValue("$repeat", JsonUtil.ToJson(r.Repeat));
        cmd.Parameters.AddWithValue("$state", JsonUtil.ToJson(new RuleStatePersist { State = r.State, RunsDone = r.RunsDone }));
        cmd.ExecuteNonQuery();
    });

    public void DeleteRule(string id) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM rules WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    public List<RuleModel> ListRules()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,enabled,macro_id,trigger_json,repeat_json,state_json FROM rules ORDER BY name ASC;";
        using var r = cmd.ExecuteReader();

        var list = new List<RuleModel>();
        while (r.Read())
        {
            var id = r.GetString(0);
            var name = r.GetString(1);
            var enabled = r.GetInt32(2) != 0;
            string? macroId = r.IsDBNull(3) ? null : r.GetString(3);
            var trigger = JsonUtil.FromJson<RoiTrigger>(r.GetString(4)) ?? new RoiTrigger();
            var repeat = JsonUtil.FromJson<RepeatPolicy>(r.GetString(5)) ?? new RepeatPolicy();
            var statePersist = JsonUtil.FromJson<RuleStatePersist>(r.GetString(6)) ?? new RuleStatePersist();

            list.Add(new RuleModel
            {
                Id = id,
                Name = name,
                Enabled = enabled,
                MacroId = macroId,
                Trigger = trigger,
                Repeat = repeat,
                State = statePersist.State,
                RunsDone = statePersist.RunsDone
            });
        }
        return list;
    }

    // Runs
    public void InsertRunStart(RunRecord run) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO runs(id,rule_id,macro_id,start_utc,end_utc,status,error_text)
VALUES($id,$rule,$macro,$start,$end,$status,$err);
";
        cmd.Parameters.AddWithValue("$id", run.Id);
        if (run.RuleId is null) cmd.Parameters.AddWithValue("$rule", DBNull.Value);
        else cmd.Parameters.AddWithValue("$rule", run.RuleId);
        if (run.MacroId is null) cmd.Parameters.AddWithValue("$macro", DBNull.Value);
        else cmd.Parameters.AddWithValue("$macro", run.MacroId);
        cmd.Parameters.AddWithValue("$start", run.StartUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$end", DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (int)run.Status);
        if (run.ErrorText is null) cmd.Parameters.AddWithValue("$err", DBNull.Value);
        else cmd.Parameters.AddWithValue("$err", run.ErrorText);
        cmd.ExecuteNonQuery();
    });

    public void UpdateRunEnd(RunRecord run) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE runs SET end_utc=$end,status=$status,error_text=$err WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$end", (run.EndUtc ?? DateTime.UtcNow).ToString("o"));
        cmd.Parameters.AddWithValue("$status", (int)run.Status);
        if (run.ErrorText is null) cmd.Parameters.AddWithValue("$err", DBNull.Value);
        else cmd.Parameters.AddWithValue("$err", run.ErrorText);
        cmd.ExecuteNonQuery();
    });

    // Timeline
    public void InsertTimelineEvent(TimelineEvent ev) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO timeline(id,utc_time,source,severity,message,details_json)
VALUES($id,$t,$src,$sev,$msg,$d);
";
        cmd.Parameters.AddWithValue("$id", ev.Id);
        cmd.Parameters.AddWithValue("$t", ev.UtcTime.ToString("o"));
        cmd.Parameters.AddWithValue("$src", (int)ev.Source);
        cmd.Parameters.AddWithValue("$sev", (int)ev.Severity);
        cmd.Parameters.AddWithValue("$msg", ev.Message);
        if (ev.Details is null) cmd.Parameters.AddWithValue("$d", DBNull.Value);
        else cmd.Parameters.AddWithValue("$d", JsonUtil.ToJson(ev.Details));
        cmd.ExecuteNonQuery();
    });

    public void InsertRuleScore(RuleScoreSnapshot score, int maxRowsPerRule = 300) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO rule_scores(id,rule_id,utc_time,score,explanation) VALUES($id,$rid,$t,$s,$e);";
            cmd.Parameters.AddWithValue("$id", IdUtil.NewId());
            cmd.Parameters.AddWithValue("$rid", score.RuleId);
            cmd.Parameters.AddWithValue("$t", score.UtcTime.ToString("o"));
            cmd.Parameters.AddWithValue("$s", score.Score);
            cmd.Parameters.AddWithValue("$e", score.Explanation);
            cmd.ExecuteNonQuery();
        }

        using (var trim = c.CreateCommand())
        {
            trim.Transaction = tx;
            trim.CommandText = @"DELETE FROM rule_scores WHERE id IN (
SELECT id FROM rule_scores WHERE rule_id=$rid ORDER BY utc_time DESC LIMIT -1 OFFSET $maxRows
);";
            trim.Parameters.AddWithValue("$rid", score.RuleId);
            trim.Parameters.AddWithValue("$maxRows", maxRowsPerRule);
            trim.ExecuteNonQuery();
        }
        tx.Commit();
    });

    public RuleScoreSnapshot? GetLatestRuleScore(string ruleId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rule_id,utc_time,score,explanation FROM rule_scores WHERE rule_id=$rid ORDER BY utc_time DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$rid", ruleId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new RuleScoreSnapshot
        {
            RuleId = r.GetString(0),
            UtcTime = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Score = r.GetDouble(2),
            Explanation = r.GetString(3)
        };
    }

    public void InsertRuleFeedback(string ruleId, RuleFeedbackKind kind) => ExecWithRetry(() =>
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO rule_feedback(id,rule_id,utc_time,feedback_kind) VALUES($id,$rid,$t,$k);";
        cmd.Parameters.AddWithValue("$id", IdUtil.NewId());
        cmd.Parameters.AddWithValue("$rid", ruleId);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$k", (int)kind);
        cmd.ExecuteNonQuery();
    });

}