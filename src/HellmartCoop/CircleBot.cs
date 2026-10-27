using UnityEngine;

namespace HellmartCoop
{
    // Test bot: walks the local player around a 2 m circle centred where it first spawned,
    // so the other instance can watch a moving puppet without anyone at the keyboard.
    public class CircleBot
    {
        private const float Radius = 2f;
        private const float Speed = 0.5f; // radians per second

        private PlayerManager centredOn;
        private Vector3 centre;
        private float angle;

        public void Update(PlayerManager player)
        {
            if (player == null)
            {
                centredOn = null;
                return;
            }
            if (player != centredOn)
            {
                centredOn = player;
                centre = player.transform.position;
                angle = 0f;
                Plugin.Log.LogInfo($"Bot: circling around {centre}");
            }
            // Unscaled time and a warp: the intro pauses movement and the spawn is boxed in by the bus
            // shelter, so walking through the CharacterController gets stuck.
            angle += Speed * Time.unscaledDeltaTime;
            var offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Radius;
            player.Warp(centre + offset);
        }
    }
}
