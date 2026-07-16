using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>Orthographic top-down camera: arrow keys pan, scroll zoom, framed to the map.
    /// (WASD is reserved for command hotkeys, AoE2-style — S is Stop.)</summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public static float PanSpeedMult = 1f; // settings knob (persisted by the main menu)

        private Camera _cam;
        private float _w, _h;
        private float _minSize = 4f, _maxSize;
        private float _targetSize;   // zoom eases toward this
        private Vector2 _panVel;      // pan momentum (world units/sec), glides to a stop
        private bool _dragging;       // middle-mouse grab-pan active
        private Vector3 _dragScreen;  // mouse position when the grab started
        private Vector3 _dragCamPos;  // camera position when the grab started

        public void Configure(MapDef map)
        {
            _cam = GetComponent<Camera>();
            _w = map.WidthCenti / 100f;
            _h = map.HeightCenti / 100f;
            _maxSize = _h * 0.6f;
            _targetSize = _maxSize;
            _cam.orthographicSize = _maxSize;
            transform.position = new Vector3(_w * 0.5f, _h * 0.5f, -10f);
        }

        private void Update()
        {
            if (_cam == null) return;
            float dt = Time.deltaTime;

            // ---- Zoom: scroll nudges a target size; the camera eases toward it exponentially.
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
                _targetSize = Mathf.Clamp(_targetSize * (1f - scroll * 3f), _minSize, _maxSize);
            float size = Mathf.Lerp(_cam.orthographicSize, _targetSize, 1f - Mathf.Exp(-14f * dt));
            _cam.orthographicSize = size;

            // ---- Middle-mouse grab-pan: hold the scroll wheel and drag to slide the map, the
            // grabbed point tracking the cursor. Takes priority over keyboard momentum.
            if (Input.GetMouseButtonDown(2)) { _dragging = true; _dragScreen = Input.mousePosition; _dragCamPos = transform.position; }
            if (_dragging && !Input.GetMouseButton(2)) _dragging = false;

            Vector3 p;
            if (_dragging)
            {
                float worldPerPixel = 2f * size / Mathf.Max(1, Screen.height);
                Vector3 d = _dragScreen - Input.mousePosition; // grab-the-map: content follows the cursor
                p = _dragCamPos + new Vector3(d.x, d.y, 0f) * worldPerPixel;
                _panVel = Vector2.zero; // no keyboard glide while grabbing
            }
            else
            {
                // ---- Pan: arrow keys set a desired velocity; actual velocity eases toward it so
                // panning accelerates in and glides to a stop instead of hard-starting/stopping.
                Vector2 dir = Vector2.zero;
                if (Input.GetKey(KeyCode.LeftArrow)) dir.x -= 1f;
                if (Input.GetKey(KeyCode.RightArrow)) dir.x += 1f;
                if (Input.GetKey(KeyCode.DownArrow)) dir.y -= 1f;
                if (Input.GetKey(KeyCode.UpArrow)) dir.y += 1f;
                if (dir.sqrMagnitude > 1f) dir.Normalize();
                Vector2 desired = dir * (size * 1.8f * PanSpeedMult);
                _panVel = Vector2.Lerp(_panVel, desired, 1f - Mathf.Exp(-12f * dt));
                p = transform.position + (Vector3)(_panVel * dt);
            }

            float mx = size * _cam.aspect, my = size;
            p.x = Mathf.Clamp(p.x, -mx * 0.5f, _w + mx * 0.5f);
            p.y = Mathf.Clamp(p.y, -my * 0.5f, _h + my * 0.5f);
            p.z = -10f;
            transform.position = p;
        }
    }
}
