using System.Collections.Generic;
using UnityEngine;

namespace HouseScan
{
    /// <summary>
    /// Where a playable level comes from. A level can be derived from a splat
    /// scan the player copied onto the headset, or mapped in the app by walking
    /// around, and nothing downstream - navigation, the hunters, the round
    /// loop - needs to know which.
    /// </summary>
    public interface ILevelSource
    {
        ScanLevelAnalysis analysis { get; }
        IReadOnlyList<Vector3> spawnPoints { get; }

        /// <summary>
        /// How wide an agent this level's geometry can actually support, in
        /// metres of radius. A level is not just a shape - it comes with a
        /// statement about how much of it is trustworthy.
        ///
        /// A splat scan measures whole rooms, so agents can be person-sized. A
        /// map built by walking only knows about the strip of floor the player's
        /// body swept, so its agents have to be narrower than the person who
        /// mapped it, or they will not fit down the corridors that walking
        /// proved were passable. Zero means "use the default".
        /// </summary>
        float agentRadius { get; }
    }
}
