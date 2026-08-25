using System.Text;
using HumanAPI;
using UnityEngine;

namespace TwilightCore.Preload;

/// <summary>
/// Machine-state diagnostics for the swap-in machine investigation: dumps the
/// live joint/physics state of every anchor-rotating machine component in the
/// current level —
/// AngularJoint (HumanAPI runtime joints), Lever, Catapult, authored
/// HingeJoint/ConfigurableJoint, plus rigidbody sleep/velocity — so a broken
/// machine can be inspected at the moment it is observed broken
/// (`twi preload mach`), and the swap itself snapshots three checkpoints
/// (post-activate / post-AfterLoad / settled) to show whether machines are
/// BORN wrong at activation or GET corrupted by the following physics steps.
/// </summary>
internal static class MachineDiagnostics
{
    /// <summary>
    /// Dump the current level's machine state. Reads
    /// <c>Game.currentLevel</c>'s hierarchy (the playing level); logs one
    /// compact line per component, grouped by type.
    /// </summary>
    public static void DumpCurrentLevel(string tag)
    {
        var level = Game.currentLevel;
        if (level == null)
        {
            Plugin.Logger.LogInfo($"[MachDiag:{tag}] no current level.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[MachDiag:{tag}] level='{level.name}'");

        // AngularJoint — HumanAPI's runtime-created ConfigurableJoints
        // (levers, servos, gates …)
        var angular = level.GetComponentsInChildren<AngularJoint>(true);
        sb.AppendLine($"  AngularJoint x{angular.Length}");
        foreach (var aj in angular)
        {
            if (aj == null) continue;
            var jb = aj.GetComponent<Rigidbody>();
            sb.AppendLine(
                $"    {Path(aj)} tgt={aj.GetTarget():0.##} val={SafeValue(aj):0.##} center={aj.centerValue:0.##} lim=[{aj.minValue:0.##},{aj.maxValue:0.##}] joint={(aj.jointCreated ? "live" : "NULL")}{(jb != null ? $" sleep={jb.IsSleeping()} vel={jb.velocity.magnitude:0.##}" : "")}");
        }

        // Lever — angle read from its joint every FixedUpdate
        var levers = level.GetComponentsInChildren<Lever>(true);
        sb.AppendLine($"  Lever x{levers.Length}");
        foreach (var lv in levers)
        {
            if (lv == null) continue;
            sb.AppendLine($"    {Path(lv)} angle={lv.angle:0.##} out={SafeOutput(lv)}");
        }

        // Catapult — the Siege rosetta stone
        var catapults = level.GetComponentsInChildren<Catapult>(true);
        if (catapults.Length > 0)
        {
            sb.AppendLine($"  Catapult x{catapults.Length}");
            foreach (var c in catapults)
            {
                if (c == null) continue;
                sb.AppendLine(
                    $"    {Path(c)} state={c.state} windlassAngle={c.windlassAngle:0.##} windlassLocalZ={c.windlass.transform.localRotation.eulerAngles.z:0.##}");
            }
        }

        // Authored hinge joints (dumpster lids, truck beds …) — .angle is the
        // LIVE joint angle: the single most direct "is it at rest" reading.
        var hinges = level.GetComponentsInChildren<HingeJoint>(true);
        sb.AppendLine($"  HingeJoint x{hinges.Length}");
        int n = 0;
        foreach (var h in hinges)
        {
            if (h == null) continue;
            if (n++ >= 24) { sb.AppendLine("    … (truncated)"); break; }
            var rb = h.GetComponent<Rigidbody>();
            sb.AppendLine(
                $"    {Path(h)} angle={h.angle:0.##} spring={(h.useSpring ? h.spring.targetPosition.ToString("0.##") : "off")} conn={(h.connectedBody != null ? h.connectedBody.name : "world")} sleep={(rb != null && rb.IsSleeping())}");
        }

        // Authored configurable joints
        var cfgs = level.GetComponentsInChildren<ConfigurableJoint>(true);
        sb.AppendLine($"  ConfigurableJoint x{cfgs.Length} (targets shown only)");
        n = 0;
        foreach (var cj in cfgs)
        {
            if (cj == null) continue;
            var e = cj.targetRotation.eulerAngles;
            if (n++ >= 24) { sb.AppendLine("    … (truncated)"); break; }
            sb.AppendLine(
                $"    {Path(cj)} targetRot=({e.x:0.##},{e.y:0.##},{e.z:0.##}) drive={cj.angularXDrive.maximumForce:0}/{cj.angularXDrive.positionSpring:0.##} conn={(cj.connectedBody != null ? cj.connectedBody.name : "world")}");
        }

        Plugin.Logger.LogInfo(sb.ToString());
    }

    private static string Path(Component c)
    {
        var t = c.transform;
        string s = t.name;
        int up = 0;
        while (t.parent != null && up < 2)
        {
            t = t.parent;
            s = t.name + "/" + s;
            up++;
        }
        return s;
    }

    private static string SafeValue(AngularJoint aj)
    {
        try { return aj.GetValue().ToString("0.##"); }
        catch { return "?"; }
    }

    private static string SafeOutput(Lever lv)
    {
        try { return lv.output.value.ToString("0.##"); }
        catch { return "?"; }
    }
}
