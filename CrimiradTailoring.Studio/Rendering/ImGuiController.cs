using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace CrimiradTailoring.Studio.Rendering
{
    /// <summary>
    /// Controller OpenGL 3.3 Core cho ImGui.NET gắn trực tiếp trên OpenTK GameWindow (hỗ trợ tiếng Việt UTF-8, theme Blender).
    /// </summary>
    public sealed class ImGuiController : IDisposable
    {
        private bool _disposed;
        private int _vao;
        private int _vbo;
        private int _ebo;
        private int _vboSize = 10000;
        private int _eboSize = 2000;
        private int _fontTexture;
        private int _shader;
        private int _attribLocTex;
        private int _attribLocProjMtx;
        private int _width;
        private int _height;

        public ImGuiController(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            IntPtr context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);

            var io = ImGui.GetIO();
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.DisplaySize = new System.Numerics.Vector2(_width, _height);

            LoadVietnameseFont(io);
            ApplyBlenderDarkTheme();
            CreateDeviceResources();
        }

        private static void LoadVietnameseFont(ImGuiIOPtr io)
        {
            string[] candidates =
            {
                @"C:\Windows\Fonts\segoeui.ttf",
                @"C:\Windows\Fonts\tahoma.ttf",
                @"C:\Windows\Fonts\arial.ttf"
            };

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    io.Fonts.AddFontFromFileTTF(path, 16.0f, null, io.Fonts.GetGlyphRangesVietnamese());
                    return;
                }
            }
            io.Fonts.AddFontDefault();
        }

        private static void ApplyBlenderDarkTheme()
        {
            var style = ImGui.GetStyle();
            style.WindowRounding = 7.0f;
            style.ChildRounding = 5.0f;
            style.FrameRounding = 5.0f;
            style.PopupRounding = 6.0f;
            style.GrabRounding = 4.0f;
            style.ScrollbarRounding = 5.0f;
            style.WindowPadding = new System.Numerics.Vector2(10, 8);
            style.FramePadding = new System.Numerics.Vector2(8, 4);
            style.ItemSpacing = new System.Numerics.Vector2(6, 5);
            style.WindowBorderSize = 1.0f;
            style.FrameBorderSize = 1.0f;

            var c = style.Colors;
            c[(int)ImGuiCol.Text] = new System.Numerics.Vector4(0.93f, 0.94f, 0.96f, 1.0f);
            c[(int)ImGuiCol.TextDisabled] = new System.Numerics.Vector4(0.55f, 0.60f, 0.72f, 1.0f);
            c[(int)ImGuiCol.WindowBg] = new System.Numerics.Vector4(0.09f, 0.106f, 0.149f, 0.88f);
            c[(int)ImGuiCol.ChildBg] = new System.Numerics.Vector4(0.067f, 0.078f, 0.114f, 0.92f);
            c[(int)ImGuiCol.PopupBg] = new System.Numerics.Vector4(0.10f, 0.12f, 0.17f, 0.97f);
            c[(int)ImGuiCol.Border] = new System.Numerics.Vector4(0.20f, 0.24f, 0.34f, 0.90f);
            c[(int)ImGuiCol.FrameBg] = new System.Numerics.Vector4(0.11f, 0.13f, 0.19f, 1.0f);
            c[(int)ImGuiCol.FrameBgHovered] = new System.Numerics.Vector4(0.17f, 0.21f, 0.30f, 1.0f);
            c[(int)ImGuiCol.FrameBgActive] = new System.Numerics.Vector4(0.22f, 0.28f, 0.40f, 1.0f);
            c[(int)ImGuiCol.MenuBarBg] = new System.Numerics.Vector4(0.086f, 0.102f, 0.141f, 0.98f);
            c[(int)ImGuiCol.Button] = new System.Numerics.Vector4(0.14f, 0.16f, 0.22f, 1.0f);
            c[(int)ImGuiCol.ButtonHovered] = new System.Numerics.Vector4(0.20f, 0.24f, 0.34f, 1.0f);
            c[(int)ImGuiCol.ButtonActive] = new System.Numerics.Vector4(0.18f, 0.42f, 0.90f, 1.0f);
            c[(int)ImGuiCol.Header] = new System.Numerics.Vector4(0.18f, 0.42f, 0.90f, 0.75f);
            c[(int)ImGuiCol.HeaderHovered] = new System.Numerics.Vector4(0.24f, 0.48f, 0.94f, 0.90f);
            c[(int)ImGuiCol.HeaderActive] = new System.Numerics.Vector4(0.18f, 0.42f, 0.90f, 1.0f);
            c[(int)ImGuiCol.SliderGrab] = new System.Numerics.Vector4(0.53f, 0.75f, 0.82f, 1.0f);
            c[(int)ImGuiCol.SliderGrabActive] = new System.Numerics.Vector4(0.32f, 0.55f, 1.0f, 1.0f);
            c[(int)ImGuiCol.CheckMark] = new System.Numerics.Vector4(0.53f, 0.75f, 0.82f, 1.0f);
            c[(int)ImGuiCol.Separator] = new System.Numerics.Vector4(0.19f, 0.23f, 0.33f, 1.0f);
        }

        private void CreateDeviceResources()
        {
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            _ebo = GL.GenBuffer();

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, _vboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, _eboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            int stride = Unsafe.SizeOf<ImDrawVert>();
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

            const string vsSrc = @"
#version 330 core
layout (location = 0) in vec2 Position;
layout (location = 1) in vec2 UV;
layout (location = 2) in vec4 Color;
uniform mat4 ProjMtx;
out vec2 Frag_UV;
out vec4 Frag_Color;
void main()
{
    Frag_UV = UV;
    Frag_Color = Color;
    gl_Position = ProjMtx * vec4(Position.xy, 0, 1);
}";

            const string fsSrc = @"
#version 330 core
in vec2 Frag_UV;
in vec4 Frag_Color;
uniform sampler2D Texture;
out vec4 Out_Color;
void main()
{
    Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
}";

            int vs = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vs, vsSrc);
            GL.CompileShader(vs);

            int fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fs, fsSrc);
            GL.CompileShader(fs);

            _shader = GL.CreateProgram();
            GL.AttachShader(_shader, vs);
            GL.AttachShader(_shader, fs);
            GL.LinkProgram(_shader);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);

            _attribLocTex = GL.GetUniformLocation(_shader, "Texture");
            _attribLocProjMtx = GL.GetUniformLocation(_shader, "ProjMtx");

            RebuildFontTexture();
        }

        private void RebuildFontTexture()
        {
            var io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);

            _fontTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            io.Fonts.SetTexID((IntPtr)_fontTexture);
            io.Fonts.ClearTexData();
        }

        public void WindowResized(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
        }

        public void Update(GameWindow wnd, float deltaSeconds)
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(_width, _height);
            io.DisplayFramebufferScale = System.Numerics.Vector2.One;
            io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : (1f / 60f);

            var mouse = wnd.MouseState;
            var kb = wnd.KeyboardState;
            io.AddMousePosEvent(mouse.X, mouse.Y);
            io.AddMouseButtonEvent(0, mouse.IsButtonDown(MouseButton.Left));
            io.AddMouseButtonEvent(1, mouse.IsButtonDown(MouseButton.Right));
            io.AddMouseButtonEvent(2, mouse.IsButtonDown(MouseButton.Middle));

            io.AddKeyEvent(ImGuiKey.ModCtrl, kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl));
            io.AddKeyEvent(ImGuiKey.ModShift, kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift));
            io.AddKeyEvent(ImGuiKey.ModAlt, kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt));

            ImGui.NewFrame();
        }

        public static void HandleMouseWheel(float offsetX, float offsetY)
        {
            ImGui.GetIO().AddMouseWheelEvent(offsetX, offsetY);
        }

        public static void HandleTextInput(int unicode)
        {
            ImGui.GetIO().AddInputCharacter((uint)unicode);
        }

        public static void HandleKeyEvent(Keys key, bool isDown)
        {
            ImGuiKey mapped = TranslateKey(key);
            if (mapped != ImGuiKey.None)
                ImGui.GetIO().AddKeyEvent(mapped, isDown);
        }

        private static ImGuiKey TranslateKey(Keys key) => key switch
        {
            Keys.Tab => ImGuiKey.Tab,
            Keys.Left => ImGuiKey.LeftArrow,
            Keys.Right => ImGuiKey.RightArrow,
            Keys.Up => ImGuiKey.UpArrow,
            Keys.Down => ImGuiKey.DownArrow,
            Keys.PageUp => ImGuiKey.PageUp,
            Keys.PageDown => ImGuiKey.PageDown,
            Keys.Home => ImGuiKey.Home,
            Keys.End => ImGuiKey.End,
            Keys.Insert => ImGuiKey.Insert,
            Keys.Delete => ImGuiKey.Delete,
            Keys.Backspace => ImGuiKey.Backspace,
            Keys.Space => ImGuiKey.Space,
            Keys.Enter or Keys.KeyPadEnter => ImGuiKey.Enter,
            Keys.Escape => ImGuiKey.Escape,
            Keys.Minus or Keys.KeyPadSubtract => ImGuiKey.Minus,
            Keys.Period or Keys.KeyPadDecimal => ImGuiKey.Period,
            Keys.D0 or Keys.KeyPad0 => ImGuiKey._0,
            Keys.D1 or Keys.KeyPad1 => ImGuiKey._1,
            Keys.D2 or Keys.KeyPad2 => ImGuiKey._2,
            Keys.D3 or Keys.KeyPad3 => ImGuiKey._3,
            Keys.D4 or Keys.KeyPad4 => ImGuiKey._4,
            Keys.D5 or Keys.KeyPad5 => ImGuiKey._5,
            Keys.D6 or Keys.KeyPad6 => ImGuiKey._6,
            Keys.D7 or Keys.KeyPad7 => ImGuiKey._7,
            Keys.D8 or Keys.KeyPad8 => ImGuiKey._8,
            Keys.D9 or Keys.KeyPad9 => ImGuiKey._9,
            Keys.A => ImGuiKey.A,
            Keys.C => ImGuiKey.C,
            Keys.F => ImGuiKey.F,
            Keys.G => ImGuiKey.G,
            Keys.R => ImGuiKey.R,
            Keys.S => ImGuiKey.S,
            Keys.V => ImGuiKey.V,
            Keys.X => ImGuiKey.X,
            Keys.Y => ImGuiKey.Y,
            Keys.Z => ImGuiKey.Z,
            _ => ImGuiKey.None
        };

        public void Render()
        {
            ImGui.Render();
            ImDrawDataPtr drawData = ImGui.GetDrawData();
            if (drawData.CmdListsCount == 0) return;

            GL.Enable(EnableCap.Blend);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.ScissorTest);

            var io = ImGui.GetIO();
            Matrix4 ortho = Matrix4.CreateOrthographicOffCenter(0f, io.DisplaySize.X, io.DisplaySize.Y, 0f, -1f, 1f);

            GL.UseProgram(_shader);
            GL.Uniform1(_attribLocTex, 0);
            GL.UniformMatrix4(_attribLocProjMtx, false, ref ortho);

            GL.BindVertexArray(_vao);

            int vtxSize = Unsafe.SizeOf<ImDrawVert>();
            int idxSize = Unsafe.SizeOf<ushort>();

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr cmdList = drawData.CmdLists[n];
                int reqVtx = cmdList.VtxBuffer.Size * vtxSize;
                int reqIdx = cmdList.IdxBuffer.Size * idxSize;

                GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
                if (reqVtx > _vboSize)
                {
                    _vboSize = reqVtx + 5000;
                    GL.BufferData(BufferTarget.ArrayBuffer, _vboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                }
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, reqVtx, cmdList.VtxBuffer.Data);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
                if (reqIdx > _eboSize)
                {
                    _eboSize = reqIdx + 2000;
                    GL.BufferData(BufferTarget.ElementArrayBuffer, _eboSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                }
                GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, reqIdx, cmdList.IdxBuffer.Data);

                for (int cmdi = 0; cmdi < cmdList.CmdBuffer.Size; cmdi++)
                {
                    ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmdi];
                    if (pcmd.UserCallback != IntPtr.Zero) continue;

                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);

                    var clip = pcmd.ClipRect;
                    int x = (int)clip.X;
                    int y = (int)(io.DisplaySize.Y - clip.W);
                    int w = (int)(clip.Z - clip.X);
                    int h = (int)(clip.W - clip.Y);
                    if (w > 0 && h > 0)
                    {
                        GL.Scissor(x, y, w, h);
                        GL.DrawElementsBaseVertex(
                            PrimitiveType.Triangles,
                            (int)pcmd.ElemCount,
                            DrawElementsType.UnsignedShort,
                            (IntPtr)(pcmd.IdxOffset * idxSize),
                            (int)pcmd.VtxOffset);
                    }
                }
            }

            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GL.DeleteVertexArray(_vao);
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_ebo);
            GL.DeleteTexture(_fontTexture);
            GL.DeleteProgram(_shader);
        }
    }
}
