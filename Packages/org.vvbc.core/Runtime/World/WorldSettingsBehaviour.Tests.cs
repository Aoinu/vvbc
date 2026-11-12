using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

namespace VVBC.World
{
    public partial class WorldSettingsBehaviour
    {
        [TsukimiTest]
        public void InspectorDefaultsMatchTheSpecification()
        {
            Assert.AreEqual(2f, walkSpeed);
            Assert.AreEqual(4f, runSpeed);
            Assert.AreEqual(2f, strafeSpeed);
            Assert.AreEqual(3f, jumpImpulse);
            Assert.AreEqual(0f, voiceDistanceNear);
            Assert.AreEqual(25f, voiceDistanceFar);
        }

        [TsukimiTest]
        public void InvalidSettingsAreRejected()
        {
            Assert.IsTrue(VoiceDistancesValid(0f, 25f));
            Assert.IsTrue(VoiceDistancesValid(5f, 5f));
            Assert.IsFalse(VoiceDistancesValid(30f, 25f), "near beyond far");
            Assert.IsFalse(VoiceDistancesValid(-1f, 25f));
            Assert.IsFalse(VoiceDistancesValid(0f, float.NaN));
            Assert.IsFalse(ValidAmount(-0.1f));
            Assert.IsFalse(ValidAmount(float.PositiveInfinity));
            Assert.IsTrue(ValidAmount(0f));
        }

        [TsukimiTest]
        public void AnInvalidPlayerIsIgnored()
        {
            OnPlayerJoined(null);
        }
    }
}
