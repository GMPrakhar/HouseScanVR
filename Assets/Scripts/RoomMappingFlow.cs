using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HouseScan
{
    /// <summary>
    /// The in-headset route from "just installed this" to "playing in my own
    /// house", with no second app, no cable and no file to copy.
    ///
    /// Put the headset on, press A, and walk around your home. The floor lights
    /// up behind you. Press A again and the space you covered becomes the level:
    /// the hunters path through your hallway and lose sight of you behind your
    /// own furniture. The map is saved, so it is a thing you do once.
    ///
    /// A pre-scanned .ply is still supported and still loads, but it is now the
    /// alternative rather than the requirement.
    /// </summary>
    public class RoomMappingFlow : MonoBehaviour
    {
        public ScanPlayerRig m_Rig;
        public RoomMappingSession m_Session;
        public MappedFloorView m_FloorView;
        public GameDirector m_Director;

        /// Starts a round as soon as a map is ready, rather than waiting to be
        /// told. Off in the probes, which drive the rounds themselves.
        public bool m_PlayWhenReady = true;

        bool m_PrevButton;
        bool m_Started;

        void Start()
        {
            if (m_Session == null) return;
            m_Session.EnsureLoaded();

            // A map already on disk means the player has done this before, and
            // should not be asked to walk their house again to play.
            if (m_Session.stage == RoomMappingSession.Stage.Complete)
            {
                Debug.Log("[Flow] Using the saved map of your house. " +
                          "Hold A for three seconds to map it again.");
                BeginPlaying();
            }
            else
            {
                Debug.Log("[Flow] Press A and walk around your home to map it.");
            }
        }

        void Update()
        {
            if (m_Session == null) return;

            bool pressed = ReadPrimaryButton();
            bool edge = pressed && !m_PrevButton;
            m_PrevButton = pressed;
            if (!edge) return;

            switch (m_Session.stage)
            {
                case RoomMappingSession.Stage.Idle:
                    m_Session.m_Rig = m_Rig;
                    m_Session.BeginMapping();
                    break;

                case RoomMappingSession.Stage.Mapping:
                    // Deliberately still refuses here rather than silently
                    // starting an unplayably small level; the session's message
                    // says how much more is needed.
                    m_Session.FinishMapping();
                    break;

                case RoomMappingSession.Stage.ReadyToFinish:
                    if (m_Session.FinishMapping())
                    {
                        m_Session.Save();
                        BeginPlaying();
                    }
                    break;

                case RoomMappingSession.Stage.Complete:
                    if (m_Director != null && !m_Director.isRoundActive)
                        m_Director.BeginRound();
                    break;
            }
        }

        void BeginPlaying()
        {
            if (m_Started) return;
            m_Started = true;

            if (m_FloorView != null) m_FloorView.Hide();
            if (m_Director == null || !m_PlayWhenReady) return;

            m_Director.m_LevelSource = m_Session;
            if (m_Rig != null) m_Rig.Bind(m_Session.analysis);
            m_Director.BeginRound();
        }

        static readonly List<InputDevice> s_Devices = new();

        static bool ReadPrimaryButton()
        {
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller, s_Devices);
            foreach (var d in s_Devices)
                if (d.TryGetFeatureValue(CommonUsages.primaryButton, out bool down) && down)
                    return true;

            // Desktop fallback, so the flow can be exercised without hardware.
            return Input.GetKey(KeyCode.M);
        }
    }
}
