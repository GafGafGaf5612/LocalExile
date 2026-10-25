public class WeaponTypeStatsTests
{
    [Theory]
    [InlineData("Bow", 9)]
    [InlineData("Quiver", null)]
    [InlineData("One Hand Axe", null)]
    [InlineData(null, null)]
    public void Only_an_active_bow_satisfies_the_native_quiver_gate(string? itemClass, int? expected)
        => Assert.Equal(expected, WeaponTypeStats.BowType(itemClass));
}
