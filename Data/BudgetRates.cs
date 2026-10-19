using HrProject_Budget.Models;
using Microsoft.EntityFrameworkCore;

namespace HrProject_Budget.Data;

/// <summary>Which budget applied on a given date: the latest change on or before that date. Dates before the first change have no budget.</summary>
public class BudgetRates
{
    public static readonly DateTime Beginning = new(2000, 1, 1);
    readonly Dictionary<(int F, int S), List<(DateTime From, int Budget)>> _map;
    BudgetRates(Dictionary<(int, int), List<(DateTime, int)>> m) => _map = m;

    public static async Task<BudgetRates> LoadAsync(AppDb db, int? factoryId = null)
    {
        var q = db.BudgetHistories.AsNoTracking().AsQueryable();
        if (factoryId != null) q = q.Where(h => h.FactoryId == factoryId);
        var rows = await q.ToListAsync();
        return new(rows.GroupBy(h => (h.FactoryId, h.SectionId))
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.EffectiveFrom).Select(h => (h.EffectiveFrom.Date, h.Budget)).ToList()));
    }

    public int Get(int factoryId, int sectionId, DateTime date)
    {
        if (!_map.TryGetValue((factoryId, sectionId), out var l)) return 0;
        var v = 0;
        foreach (var (from, b) in l) { if (from <= date.Date) v = b; else break; }
        return v;
    }

    /// <summary>Budget of every factory/section pair on that date.</summary>
    public Dictionary<(int, int), int> At(DateTime date) => _map.Keys.ToDictionary(k => k, k => Get(k.F, k.S, date));
}
