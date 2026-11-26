using Tsukimi;
using UnityEngine;

namespace VVBC.Locomotion
{
    public partial class FenceJumpZoneBehaviour
    {
        [TsukimiTest]
        public void EnterAndExitCountTheLocalPlayerOnce()
        {
            Assert.IsFalse(locomotion.InFenceZone());
            Enter();
            Enter();
            Assert.IsTrue(IsInside());
            Assert.IsTrue(locomotion.InFenceZone(), "inside");
            Exit();
            Assert.IsFalse(locomotion.InFenceZone(), "a second enter of the same zone did not count twice");
            Exit();
            Assert.IsFalse(locomotion.InFenceZone(), "an exit without an enter (a teleport) never goes below zero");
        }

        [TsukimiTest]
        public void ADisabledZoneLetsGoOfThePlayer()
        {
            Enter();
            OnDisable();
            Assert.IsFalse(IsInside());
            Assert.IsFalse(locomotion.InFenceZone());
            OnDisable();
            Assert.IsFalse(locomotion.InFenceZone(), "disabling twice changes nothing");
        }

        [TsukimiTest]
        public void TheZoneChoosesTheFenceImpulseOnlyWhenOneIsSet()
        {
            locomotion.SetJumpImpulse(3f);
            Enter();
            Assert.AreEqual(3f, locomotion.EffectiveJumpImpulse(), "no Jump perk: the normal jump inside the zone");
            locomotion.SetFenceJumpImpulse(5f);
            Assert.AreEqual(5f, locomotion.EffectiveJumpImpulse(), "Jump I inside: the fence jump");
            Exit();
            Assert.AreEqual(3f, locomotion.EffectiveJumpImpulse(), "outside: the normal jump");
        }
    }
}
