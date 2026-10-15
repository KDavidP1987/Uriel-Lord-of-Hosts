using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProjectM;
using Unity.Entities;

namespace Uriel.Services;

/// <summary>
/// Castle Events scaling (docs/features/CASTLE_EVENTS.md › Scaling): a unit's level and stat multipliers for one run,
/// set in one short option string such as <c>hard</c>, <c>auto+2,hp*2</c> or <c>level=60,dmg*1.5,speed*1.2</c>.
///
/// Levels are written to UnitLevel; the game rescales the unit's base stats from it (Nyarlathotep lessons §1 item 9).
/// A V Blood keeps its own level. Multipliers ride on the unit's marker buff as ModifyUnitStatBuff_DOTS MultiplyBaseAdd
/// entries (value = multiplier - 1), never as direct Health or power writes, which the game overwrites after a spawn
/// (lessons §1 item 8, §8). They live and die with the unit.
/// </summary>
internal sealed record UnitScaling(
    int Level,          // > 0: every unit at this level
    int LevelAdd,       // added to the unit's level (after the template's level)
    bool Auto,          // level = the highest player level on the plot at start, + LevelAdd
    float Health,
    float Physical,
    float Spell,
    float Speed,
    float AttackSpeed)
{
    public static readonly UnitScaling None = new(0, 0, false, 1f, 1f, 1f, 1f, 1f);

    public bool IsNone => this == None;

    /// <summary>Named starting points; later tokens adjust them (<c>hard,speed*1</c>).</summary>
    static readonly Dictionary<string, UnitScaling> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["easy"] = None with { Health = 0.75f, Physical = 0.75f, Spell = 0.75f },
        ["normal"] = None,
        ["hard"] = None with { Health = 1.5f, Physical = 1.25f, Spell = 1.25f },
        ["brutal"] = None with { Health = 2.5f, Physical = 1.5f, Spell = 1.5f, Speed = 1.1f, AttackSpeed = 1.15f },
    };

    public static string PresetNames => string.Join(", ", Presets.Keys);

    /// <summary>Apply the scaling tokens of <paramref name="tokens"/> on top of <paramref name="start"/>. A token is a
    /// preset (easy, normal, hard, brutal), a level (<c>level=60</c>, <c>level+5</c>, <c>level-3</c>, <c>auto</c>,
    /// <c>auto+2</c>), or a multiplier <c>stat*x</c> (also <c>stat=x</c>, <c>statx</c>) for hp, dmg (physical and
    /// spell), pp, sp, speed, aspd. Multipliers are 0.1-10; levels 1-200.</summary>
    public static bool TryApply(UnitScaling start, IEnumerable<string> tokens, out UnitScaling result, out string message)
    {
        result = start;
        message = null;
        foreach (var raw in tokens)
        {
            string t = raw.Trim().ToLowerInvariant();
            if (t.Length == 0) continue;
            if (Presets.TryGetValue(t, out var preset))
            {
                result = preset with { Level = result.Level, LevelAdd = result.LevelAdd, Auto = result.Auto };
                continue;
            }
            if (t == "auto") { result = result with { Auto = true, Level = 0 }; continue; }
            if (t.StartsWith("auto+") || t.StartsWith("auto-"))
            {
                if (!int.TryParse(t.AsSpan(4), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int add)) { message = $"Bad level '{raw}'."; return false; }
                result = result with { Auto = true, Level = 0, LevelAdd = add };
                continue;
            }
            if (t.StartsWith("level") || t.StartsWith("lvl"))
            {
                string rest = t.StartsWith("level") ? t[5..] : t[3..];
                if (rest.StartsWith("=") && rest[1..] == "auto") { result = result with { Auto = true, Level = 0 }; continue; }
                if (rest.Length < 2 || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int n)) { message = $"Bad level '{raw}': use level=60, level+5 or level-3."; return false; }
                switch (rest[0])
                {
                    case '=':
                        if (n < 1 || n > 200) { message = "A level is 1-200."; return false; }
                        result = result with { Level = n, Auto = false };
                        break;
                    case '+': result = result with { LevelAdd = n }; break;
                    case '-': result = result with { LevelAdd = -n }; break;
                    default: message = $"Bad level '{raw}'."; return false;
                }
                continue;
            }
            int op = t.IndexOfAny(new[] { '*', '=', 'x' });
            if (op <= 0) { message = $"Unknown scaling '{raw}'. Use a preset ({PresetNames}), level=N, level+N, auto, or hp|dmg|pp|sp|speed|aspd*X."; return false; }
            string stat = t[..op];
            if (!float.TryParse(t[(op + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || x < 0.1f || x > 10f)
            {
                message = $"Bad multiplier in '{raw}': a number from 0.1 to 10.";
                return false;
            }
            switch (stat)
            {
                case "hp": case "health": result = result with { Health = x }; break;
                case "dmg": case "damage": case "power": result = result with { Physical = x, Spell = x }; break;
                case "pp": case "physical": result = result with { Physical = x }; break;
                case "sp": case "spell": result = result with { Spell = x }; break;
                case "speed": case "move": case "ms": result = result with { Speed = x }; break;
                case "aspd": case "attackspeed": case "as": result = result with { AttackSpeed = x }; break;
                default: message = $"Unknown stat '{stat}' in '{raw}': hp, dmg, pp, sp, speed or aspd."; return false;
            }
        }
        return true;
    }

    /// <summary>The level a unit gets: the scaling's level, else its own (template or prefab) level plus LevelAdd.
    /// 0 = leave it.</summary>
    public int LevelFor(int ownLevel)
    {
        if (Level > 0) return Level;
        if (LevelAdd == 0 || ownLevel <= 0) return 0;
        return Math.Clamp(ownLevel + LevelAdd, 1, 200);
    }

    /// <summary>The stat modifiers this scaling puts on a unit's marker buff.</summary>
    public IEnumerable<(UnitStatType Stat, float Multiplier)> Modifiers()
    {
        if (Health != 1f) yield return (UnitStatType.MaxHealth, Health);
        if (Physical != 1f) yield return (UnitStatType.PhysicalPower, Physical);
        if (Spell != 1f) yield return (UnitStatType.SpellPower, Spell);
        if (Speed != 1f) yield return (UnitStatType.MovementSpeed, Speed);
        if (AttackSpeed != 1f)
        {
            yield return (UnitStatType.PrimaryAttackSpeed, AttackSpeed);
            yield return (UnitStatType.AbilityAttackSpeed, AttackSpeed);
        }
    }

    /// <summary>Write the modifiers into the marker buff's stat buffer (cleared first by the marker recipe).</summary>
    public void WriteModifiers(Entity markerBuff)
    {
        var mods = Modifiers().ToList();
        if (mods.Count == 0) return;
        var buffer = markerBuff.Has<ModifyUnitStatBuff_DOTS>()
            ? Core.EntityManager.GetBuffer<ModifyUnitStatBuff_DOTS>(markerBuff)
            : Core.EntityManager.AddBuffer<ModifyUnitStatBuff_DOTS>(markerBuff);
        foreach (var (stat, x) in mods)
            buffer.Add(new ModifyUnitStatBuff_DOTS
            {
                StatType = stat,
                ModificationType = ModificationType.MultiplyBaseAdd,
                Value = x - 1f,
                Modifier = 1,
                IncreaseByStacks = false,
                ValueByStacks = 0,
                Priority = 0,
                Id = ModificationIDs.Create().NewModificationId(),
            });
    }

    static string X(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public override string ToString()
    {
        if (IsNone) return "none";
        var parts = new List<string>();
        if (Auto) parts.Add(LevelAdd == 0 ? "auto" : $"auto{(LevelAdd > 0 ? "+" : "")}{LevelAdd}");
        else if (Level > 0) parts.Add($"level={Level}");
        else if (LevelAdd != 0) parts.Add($"level{(LevelAdd > 0 ? "+" : "")}{LevelAdd}");
        if (Health != 1f) parts.Add($"hp*{X(Health)}");
        if (Physical != 1f && Physical == Spell) parts.Add($"dmg*{X(Physical)}");
        else
        {
            if (Physical != 1f) parts.Add($"pp*{X(Physical)}");
            if (Spell != 1f) parts.Add($"sp*{X(Spell)}");
        }
        if (Speed != 1f) parts.Add($"speed*{X(Speed)}");
        if (AttackSpeed != 1f) parts.Add($"aspd*{X(AttackSpeed)}");
        return string.Join(",", parts);
    }
}
