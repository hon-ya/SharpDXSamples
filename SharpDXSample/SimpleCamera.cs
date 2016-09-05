using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharpDXSample
{
    using SharpDX;
    using SharpDX.Windows;

    public class SimpleCamera
    {
        struct KeysPressedStruct
        {
            public bool W;
            public bool A;
            public bool S;
            public bool D;

            public bool Left;
            public bool Right;
            public bool Up;
            public bool Down;
        }

        private Vector3 InitialPosition;
        private Vector3 LookDirection;
        private float Pitch;
        private Vector3 Position;
        private float Yaw;
        private KeysPressedStruct KeysPressed;

        public float MoveSpeed { get; set; } = 20.0f;
        public float TurnSpeed { get; set; } = MathUtil.PiOverTwo;

        public SimpleCamera()
        {
        }

        public void Initialize(Vector3 position)
        {
            InitialPosition = position;

            Reset();
        }

        public void RegisterHandler(RenderForm form)
        {
            form.KeyDown += OnKeyDown;
            form.KeyUp += OnKeyUp;
        }

        public void UnregisterHandler(RenderForm form)
        {
            form.KeyDown -= OnKeyDown;
            form.KeyUp -= OnKeyUp;
        }

        public void Reset()
        {
            Position = InitialPosition;
            Yaw = MathUtil.Pi;
            Pitch = 0.0f;
            LookDirection = new Vector3(0.0f, 0.0f, -1.0f);
        }

        public void Update(TimeSpan elapsedTime)
        {
            var move = Vector3.Zero;

            if(KeysPressed.A)
            {
                move.X -= 1.0f;
            }
            if(KeysPressed.D)
            {
                move.X += 1.0f;
            }
            if(KeysPressed.W)
            {
                move.Z -= 1.0f;
            }
            if(KeysPressed.S)
            {
                move.Z += 1.0f;
            }

            if(Math.Abs(move.X) > 0.1f && Math.Abs(move.Z) > 0.1f)
            {
                var vector = Vector3.Normalize(move);
                move.X = vector.X;
                move.Z = vector.Z;
            }

            var moveInterval = (float)(MoveSpeed * elapsedTime.TotalSeconds);
            var rotateInterval = (float)(TurnSpeed * elapsedTime.TotalSeconds);

            if (KeysPressed.Left)
            {
                Yaw += rotateInterval;
            }
            if (KeysPressed.Right)
            {
                Yaw -= rotateInterval;
            }
            if (KeysPressed.Up)
            {
                Pitch += rotateInterval;
            }
            if (KeysPressed.Down)
            {
                Pitch -= rotateInterval;
            }

            Pitch = Math.Min(Pitch, MathUtil.PiOverFour);
            Pitch = Math.Max(-MathUtil.PiOverFour, Pitch);

            var r = Math.Cos(Pitch);

            var x = r * (move.X * -Math.Cos(Yaw) - move.Z * Math.Sin(Yaw));
            var z = r * (move.X * Math.Sin(Yaw) - move.Z * Math.Cos(Yaw));
            var y = - move.Z * Math.Sin(Pitch);

            Position.X += (float)(x * moveInterval);
            Position.Y += (float)(y * moveInterval);
            Position.Z += (float)(z * moveInterval);

            LookDirection.X = (float)(r * Math.Sin(Yaw));
            LookDirection.Y = (float)(Math.Sin(Pitch));
            LookDirection.Z = (float)(r * Math.Cos(Yaw));
        }

        public Matrix GetViewMatrix()
        {
            return Matrix.LookAtRH(Position, Position + LookDirection, Vector3.Up);
        }

        public Matrix GetProjectionMatrix(float fov, float aspectRatio, float nearPlane = 1.0f, float farPlane = 1000.0f)
        {
            return Matrix.PerspectiveFovRH(0.8f, aspectRatio, nearPlane, farPlane);
        }

        public void OnKeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            switch(e.KeyCode)
            {
                case System.Windows.Forms.Keys.W:
                    KeysPressed.W = true;
                    break;
                case System.Windows.Forms.Keys.A:
                    KeysPressed.A = true;
                    break;
                case System.Windows.Forms.Keys.S:
                    KeysPressed.S = true;
                    break;
                case System.Windows.Forms.Keys.D:
                    KeysPressed.D = true;
                    break;
                case System.Windows.Forms.Keys.Left:
                    KeysPressed.Left = true;
                    break;
                case System.Windows.Forms.Keys.Right:
                    KeysPressed.Right = true;
                    break;
                case System.Windows.Forms.Keys.Up:
                    KeysPressed.Up = true;
                    break;
                case System.Windows.Forms.Keys.Down:
                    KeysPressed.Down = true;
                    break;
                case System.Windows.Forms.Keys.Space:
                    Reset();
                    break;
            }
        }

        public void OnKeyUp(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case System.Windows.Forms.Keys.W:
                    KeysPressed.W = false;
                    break;
                case System.Windows.Forms.Keys.A:
                    KeysPressed.A = false;
                    break;
                case System.Windows.Forms.Keys.S:
                    KeysPressed.S = false;
                    break;
                case System.Windows.Forms.Keys.D:
                    KeysPressed.D = false;
                    break;
                case System.Windows.Forms.Keys.Left:
                    KeysPressed.Left = false;
                    break;
                case System.Windows.Forms.Keys.Right:
                    KeysPressed.Right = false;
                    break;
                case System.Windows.Forms.Keys.Up:
                    KeysPressed.Up = false;
                    break;
                case System.Windows.Forms.Keys.Down:
                    KeysPressed.Down = false;
                    break;
            }
        }
    }
}
