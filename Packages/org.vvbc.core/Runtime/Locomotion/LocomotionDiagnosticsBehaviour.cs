using Tsukimi;
using TMPro;
using UnityEngine;
using VRC.SDKBase;

namespace VVBC.Locomotion
{
    // Validation-scene readout for the Station locomotion test, refreshed a few times a
    // second. Interact writes the same snapshot to the log. Observes only: it never drives
    // a Station. "rel" values are in the Station's local frame (its rotation is fixed for
    // the session), so room-scale walking and leaning show up as changes there.
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public partial class LocomotionDiagnosticsBehaviour : TsukimiBehaviour
    {
        [SerializeField] private TMP_Text label;
        [Tooltip("Seconds between refreshes (0.1 to 0.2 gives 5 to 10 Hz).")]
        [SerializeField] private float refreshInterval = 0.15f;

        private LocomotionControllerBehaviour _controller;
        private int _measuredSession = -1;
        private Vector3 _sessionStartPosition;
        private Vector3 _lastPosition;
        private float _lastSampleTime;
        private float _measuredSpeed;
        private float _measuredPath;
        private string _text = "";

        // Copied by Prepare before Compose: a read of another Behaviour from inside
        // Compose would save and restore all of its locals.
        private int _state;
        private int _session;
        private string _lastEvent = "";
        private int _entered;
        private int _exited;
        private int _respawned;
        private Vector3 _stationPosition;
        private Vector3 _anchorPosition;
        private Vector2 _stick;
        private float _modelSpeed;
        private float _stationSpeed;
        private float _commanded;

        private void Start()
        {
            SendCustomEventDelayedSeconds(nameof(_Refresh), refreshInterval);
        }

        public override void Interact()
        {
            Prepare();
            Debug.Log("[VVBC Locomotion] snapshot\n" + Compose());
        }

        public void _Refresh()
        {
            Prepare();
            _text = Compose();
            if (label != null) label.text = _text;
            SendCustomEventDelayedSeconds(nameof(_Refresh), refreshInterval);
        }

        private string Compose()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return "no local player";
            LocomotionControllerBehaviour controller = _controller;
            string text = "<b>Network</b>  local " + local.displayName + " #" + local.playerId
                + (local.IsUserInVR() ? " VR" : " Desktop") + "\n";
            if (controller == null) return text + "LocomotionPlayerObject not found\n" + RemoteLines(local);

            Vector3 playerPosition = local.GetPosition();
            Sample(playerPosition);
            Transform root = controller.transform;
            VRCPlayerApi owner = Networking.GetOwner(controller.gameObject);
            text += "owner " + (Utilities.IsValid(owner) ? owner.displayName + " #" + owner.playerId : "?")
                + "  root " + controller.gameObject.name + "\n";

            text += "<b>Station</b>  state " + StateName(_state) + "  session " + _session
                + "  last " + _lastEvent
                + "  in/out/respawn " + _entered + "/" + _exited + "/" + _respawned + "\n";
            text += "root " + V(root.position) + "  station " + V(_stationPosition)
                + "  anchor " + V(_anchorPosition) + "\n";

            text += "<b>Movement</b>  stick " + F(_stick.x) + "," + F(_stick.y)
                + "  model " + F(_modelSpeed) + " m/s"
                + "  station " + F(_stationSpeed) + " m/s"
                + "  player " + F(_measuredSpeed) + " m/s\n";
            text += "commanded " + F(_commanded) + " m  player path " + F(_measuredPath)
                + " m  net " + F(Horizontal(playerPosition - _sessionStartPosition).magnitude) + " m\n";

            VRCPlayerApi.TrackingData origin = local.GetTrackingData(VRCPlayerApi.TrackingDataType.Origin);
            VRCPlayerApi.TrackingData avatarRoot = local.GetTrackingData(VRCPlayerApi.TrackingDataType.AvatarRoot);
            VRCPlayerApi.TrackingData head = local.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            text += "<b>Tracking</b> (rel = Station local)\n";
            text += "origin " + V(origin.position) + " rel " + V(root.InverseTransformPoint(origin.position)) + "\n";
            text += "avatarRoot " + V(avatarRoot.position) + " rel " + V(root.InverseTransformPoint(avatarRoot.position)) + "\n";
            text += "head " + V(head.position) + " rel " + V(root.InverseTransformPoint(head.position)) + "\n";
            text += "GetPosition " + V(playerPosition) + " rel " + V(root.InverseTransformPoint(playerPosition))
                + "  velocity " + V(local.GetVelocity()) + "\n";
            text += "avatarRoot-origin " + V(avatarRoot.position - origin.position)
                + "  head-avatarRoot " + V(head.position - avatarRoot.position) + "\n";
            return text + RemoteLines(local);
        }

        // Player speed and path length from GetPosition between refreshes; both restart
        // with each Station session so they compare with the commanded distance.
        // Resolves the local controller and copies what Compose shows.
        private void Prepare()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            LocomotionControllerBehaviour controller = ResolveController(local);
            if (controller == null) return;
            _state = controller.State;
            _session = controller.Session;
            _lastEvent = controller.LastEvent;
            _entered = controller.EnteredCount;
            _exited = controller.ExitedCount;
            _respawned = controller.RespawnCount;
            _stationPosition = controller.StationPosition;
            _anchorPosition = controller.GameplayAnchorPosition;
            _stick = controller.Stick;
            _modelSpeed = controller.ModelVelocity.magnitude;
            _stationSpeed = controller.StationSpeed;
            _commanded = controller.CommandedDistance;
        }

        private void Sample(Vector3 position)
        {
            float now = Time.time;
            if (_session != _measuredSession)
            {
                _measuredSession = _session;
                _sessionStartPosition = position;
                _measuredPath = 0f;
                _measuredSpeed = 0f;
            }
            else if (now > _lastSampleTime)
            {
                float step = Horizontal(position - _lastPosition).magnitude;
                _measuredSpeed = step / (now - _lastSampleTime);
                if (_state == LocomotionControllerBehaviour.StateActive) _measuredPath += step;
            }
            _lastPosition = position;
            _lastSampleTime = now;
        }

        // Each remote player against its own MovementRoot as ObjectSync delivers it here.
        private string RemoteLines(VRCPlayerApi local)
        {
            VRCPlayerApi[] players = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
            VRCPlayerApi.GetPlayers(players);
            string text = "<b>Remote</b>\n";
            for (int i = 0; i < players.Length; i++)
            {
                VRCPlayerApi player = players[i];
                if (!Utilities.IsValid(player) || player.isLocal) continue;
                LocomotionControllerBehaviour controller = FindController(player);
                text += player.displayName + " #" + player.playerId;
                if (controller == null) { text += " no LocomotionPlayerObject\n"; continue; }
                Vector3 position = player.GetPosition();
                text += " root " + V(controller.transform.position) + " player " + V(position)
                    + " gap " + F(Horizontal(position - controller.transform.position).magnitude) + " m\n";
            }
            return text;
        }

        private LocomotionControllerBehaviour ResolveController(VRCPlayerApi local)
        {
            if (_controller == null) _controller = FindController(local);
            return _controller;
        }

        private static LocomotionControllerBehaviour FindController(VRCPlayerApi player)
        {
            GameObject[] objects = Networking.GetPlayerObjects(player);
            if (objects == null) return null;
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null) continue;
                LocomotionControllerBehaviour candidate = objects[i].GetComponentInChildren<LocomotionControllerBehaviour>();
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static string StateName(int state)
        {
            if (state == LocomotionControllerBehaviour.StateActive) return "Active";
            if (state == LocomotionControllerBehaviour.StateEntering) return "Entering";
            return "Idle";
        }

        private static Vector3 Horizontal(Vector3 v) { return new Vector3(v.x, 0f, v.z); }
        private static string F(float value) { return value.ToString("F2"); }
        private static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }
    }
}
