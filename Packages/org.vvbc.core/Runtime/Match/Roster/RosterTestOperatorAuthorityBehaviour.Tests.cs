using Tsukimi;

public partial class RosterTestOperatorAuthorityBehaviour
{
    [TsukimiTest]
    public void OnlyListedNamesAreOperators()
    {
        operatorDisplayNames = new string[0];
        Assert.IsFalse(IsListed("Alice"), "an empty list grants nobody");
        Assert.IsFalse(IsMatchOperator(1), "an empty list grants nobody, whoever asks");
        operatorDisplayNames = new string[] { "Alice", "審判" };
        Assert.IsTrue(IsListed("Alice"));
        Assert.IsTrue(IsListed("審判"));
        Assert.IsFalse(IsListed("alice"), "names match exactly");
        Assert.IsFalse(IsListed(""));
        Assert.IsFalse(IsListed(null));
        Assert.IsFalse(IsMatchOperator(0));
    }
}
