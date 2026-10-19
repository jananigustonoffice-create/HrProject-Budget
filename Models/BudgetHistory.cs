namespace HrProject_Budget.Models;

/// <summary>A budget that applies from <see cref="EffectiveFrom"/> onwards (until the next row for the same factory and section).</summary>
public class BudgetHistory
{
    public int Id { get; set; }
    public int FactoryId { get; set; }
    public int SectionId { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public int Budget { get; set; }
}
