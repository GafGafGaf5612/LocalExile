/// <summary>The equipped weapon shape that block passives depend on (see PassiveBonus).</summary>
readonly record struct WeaponProfile(bool Shield, bool DualWield, bool Staff, bool Bow, bool DualClaws);
