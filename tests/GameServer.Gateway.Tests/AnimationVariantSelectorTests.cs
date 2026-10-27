namespace GameServer.Gateway.Tests;

public class AnimationVariantSelectorTests
{
    // Independently replayed from 3.29.3b's initializer/transition/tempering instructions. These catch a stock
    // TinyMT32 implementation, the wrong shift direction, missing uint wrap, or the wrong warm-up count.
    [Theory]
    [InlineData(0u, 3762700985u, 711479187u, 552704151u)]
    [InlineData(1u, 2987096583u, 3822264440u, 3314259309u)]
    [InlineData(4097u, 3101734177u, 585665513u, 811100496u)]
    [InlineData(65535u, 2495494237u, 3990337031u, 162047813u)]
    public void Native_random_matches_literal_instruction_vectors(uint seed, uint first, uint second, uint third)
    {
        var random = new AnimationVariantSelector.NativeRandom(seed);
        Assert.Equal(first, random.NextUInt());
        Assert.Equal(second, random.NextUInt());
        Assert.Equal(third, random.NextUInt());
    }

    [Theory]
    [InlineData(4097u, 3)]
    [InlineData(4098u, 1)]
    [InlineData(4099u, 1)]
    [InlineData(4100u, 3)]
    public void Four_entry_pool_matches_native_ordinary_wand_captures(uint sequence, int selected)
    {
        // ACT order: attack1_wand, attack2_wand, attack3_wand, attack4_wand. Ground RMB captures
        // artifacts/wand-ai-validation/wand-choice-click-1..4.json use the same zero-extended input sequences.
        Assert.Equal(selected, AnimationVariantSelector.Select(sequence, [1, 1, 1, 1]));
    }

    [Fact]
    public void Bound_one_does_not_consume_a_random_word()
    {
        var random = new AnimationVariantSelector.NativeRandom(4097);
        Assert.Equal(0u, random.Uniform(1));
        Assert.Equal(3101734177u, random.NextUInt());
    }

    [Fact]
    public void Empty_zero_weight_and_overflowing_pools_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => AnimationVariantSelector.Select(1, []));
        Assert.Throws<ArgumentException>(() => AnimationVariantSelector.Select(1, [0, 0]));
        Assert.Throws<OverflowException>(() => AnimationVariantSelector.Select(1, [uint.MaxValue, 1]));
        var random = new AnimationVariantSelector.NativeRandom(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => random.Uniform(0));
        Assert.Equal(1, AnimationVariantSelector.Select(1, [0, 7, 0]));
    }
}
