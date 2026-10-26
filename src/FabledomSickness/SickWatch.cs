using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomSickness
{
    // Diagnostic (Jess, 2026-10-18): Henry Myers died of sickness after 30 days while the hospital had free beds, and
    // the daily line showed two sick fablings "without" a bed for weeks. The game's sick branch retries the hospital
    // every loop, so a fabling that never arrives is one that cannot: no path, stuck inside, under the map, on a
    // mission. One line a day per sick fabling without a bed, with what might explain it.
    internal static class SickWatch
    {
        internal static void Daily()
        {
            try
            {
                if (!Plugin.LogWaiting.Value || Worker.Instances == null) return;
                var open = Hospital.Instances != null ? Hospital.Instances.Where(h => h != null && h.HasRoomForMorePatients() && (h.closeable == null || !h.closeable.IsClosed())).ToList() : new List<Hospital>();
                foreach (var w in Worker.Instances)
                {
                    if (w == null || w.needs == null || !w.needs.isSick || w.needs.hospital != null) continue;
                    if (w.unit == null || w.unit.health == null || w.unit.health.isDead) continue;
                    if (w.needs.daysSick < Plugin.LogWaitingAfterDays.Value) continue;
                    string name; try { name = w.unit.GetLocalizedBirthName(); } catch { name = w.name; }
                    string job = "none"; try { var p = w.GetProfessionData(); if (p != null) job = p.key; } catch { }
                    string home = "none"; try { var h = w.resident != null ? w.resident.housing : null; if (h != null && h.worldObject != null) home = h.worldObject.data.name; } catch { }
                    var pos = w.transform.position;
                    float dist = -1f; string hospital = "none open";
                    if (open.Count > 0)
                    {
                        var near = open.OrderBy(h => (h.transform.position - pos).sqrMagnitude).First();
                        dist = Vector3.Distance(near.transform.position, pos);
                        hospital = $"{near.GetPatientCount()}/{near.GetCurrentCapacity()} patients, {dist:0} away";
                    }
                    string tree = "?";
                    try
                    {
                        var btc = w.GetComponent<BehaviorTreeController>();
                        tree = btc == null ? "no controller" : !btc.enabled ? "controller off" : "running";
                    }
                    catch { }
                    Plugin.Log.LogInfo($"[sickness] waiting: {name} ({w.fablingClass}, {job}, home {home}) sick {w.needs.daysSick:0} days, treated {w.needs.daysTreated:0}; at ({pos.x:0},{pos.y:0.0},{pos.z:0}), inside {w.unit.isInside}, on mission {w.unit.isOnMission}, tree {tree}; nearest open hospital {hospital}.");
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[sickness] sick watch: " + ex.Message); }
        }
    }
}
