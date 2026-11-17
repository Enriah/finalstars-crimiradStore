# Crimirad Tailoring Studio

Ứng dụng desktop thiết kế, điêu khắc rập vải 3D và ướm trang phục lên mô hình nhân vật (**CrimiradTailoring**), xây dựng bằng C# / .NET với OpenGL 3.3 (`OpenTK 4.9.4`) và giao diện `ImGui.NET 1.91.6.1`.

## Cấu trúc dự án

- `CrimiradTailoring.sln` — Solution chính của dự án.
- `CrimiradTailoring.Core/` (`netstandard2.1`) — Thư viện lõi xử lý hình học vải và mô phỏng trên nhân vật:
  - `Engine/ClothSculptEngine.cs` — Tạo lưới tứ giác, chọn vùng, cắt tách/khoét lỗ, chia mịn Catmull-Clark, biến dạng theo điểm/cạnh điều khiển và cạnh biên (`BorderEdge`), ràng buộc liên kết và nhập/xuất `.obj`.
  - `Engine/CharacterEngine.cs` — Dựng Mannequin mặc định, chuẩn hóa mesh nhân vật, đo thước dây bám cong bề mặt (Geodesic Surface Projection), giải ôm dáng theo từng lớp (`Hugging` / `Fitting`) và tính che khuất giữa các lớp (`ComputeLayerOcclusionMasks`).
  - `Engine/CharacterBvh.cs` — Cây BVH AABB tam giác phục vụ Raycast và truy vấn ClosestPoint thời gian thực.
  - `Models/ClothSculptModels.cs`, `Models/CharacterModels.cs`, `Models/Primitives.cs` — Các model dữ liệu cho vải, nhân vật, điểm neo, thước đo và cache topology.
- `CrimiradTailoring.Studio/` (`net9.0-windows`) — Ứng dụng desktop OpenGL + ImGui:
  - `Program.cs`, `StudioWindow.cs`, `StudioWindow.CharacterViewport.cs` — Cửa sổ chính, xử lý tương tác và hai viewport `Fabric` / `Character`.
  - `IO/CharacterModelImporter.cs` — Bộ nạp model nhân vật thuần C# hỗ trợ `.glb`, `.gltf` (kèm node hierarchy & skinning pose), `.obj`, Binary `.fbx` và ASCII `.fbx`.
  - `Rendering/GlClothViewportRenderer.cs`, `Rendering/GlCharacterViewportRenderer.cs`, `Rendering/ImGuiController.cs` — Bộ render OpenGL và ImGui.
  - `Platform/NativeFileDialog.cs` — Hộp thoại mở/lưu file native trên Windows.
  - `Assets/Girl_Base.fbx` — Model nhân vật cơ sở mặc định.

## Tính năng chính

### 1. Fabric Viewport (Thiết kế & Điêu khắc rập vải)
- **Khởi tạo & hiển thị lưới vải 3D**: Tạo tấm vải lưới tứ giác (mặc định `24x24`), tùy chỉnh chế độ shading (`Smooth`, `AutoSmooth`, `Flat`), wireframe overlay, lưới sàn và gizmo điều hướng 6 trục.
- **Chia mịn bề mặt không phá hủy**: Hỗ trợ chia mịn Catmull-Clark ở 3 mức (`Gốc`, `4x`, `16x`) kèm tùy chọn cân bằng lưới khi uốn (`EqualizeGridOnBend`).
- **Chọn vùng & thành phần (`Select`)**: Chọn vùng theo hình Vuông (`Box`), Tròn (`Circle`), Nón (`Cone`), hoặc chọn trực tiếp mảnh vải (`ClothObject`), điểm điều khiển (`Point`), cạnh điều khiển (`Edge`) và cạnh biên thực tế (`BorderEdge`).
- **Cắt vải (`Cut`)**: Tách vùng đã chọn thành mảnh mới (`ExtractPiece`) hoặc khoét lỗ (`RemoveHole`).
- **Điêu khắc điểm, cạnh & cạnh biên**:
  - Tạo `ControlPoint` để uốn vùng hình cầu, hoặc nối 2 điểm thành `ControlEdge` để uốn vùng hình trụ với tùy chỉnh bán kính (`Radius`) và độ uốn (`Bending`).
  - Biến dạng cạnh biên (`BorderEdge`) để tạo hình thang, xoay hoặc uốn cong đường biên.
- **Biến đổi & Liên kết**: Di chuyển (`Move`), xoay (`Rotate`), phóng to/thu nhỏ (`Scale`), và nối điểm/cạnh (`CreateEdge`, `Snap`, `Hang`).

### 2. Character Viewport (Ướm đồ & Đo kích thước trên nhân vật)
- **Nạp mô hình nhân vật 3D**: Tự động nạp `Assets/Girl_Base.fbx` (hoặc fallback sang Mannequin giải phẫu mặc định), đồng thời hỗ trợ import từ `.glb`, `.gltf`, `.obj`, `.fbx`.
- **Thước dây đo bề mặt (`Ruler`)**: Đặt các điểm trên bề mặt nhân vật để đo chiều dài bám theo độ cong hình học (hỗ trợ cả đường hở và vòng kín).
- **Điểm neo & gắn vải (`Anchor` + Kéo-thả)**: Đặt điểm neo trên bề mặt nhân vật (`CharacterAnchorPoint`), kéo-thả mảnh vải từ Outliner vào `CharacterViewport` để gắn 1-1 với lưới rập ở `FabricViewport`.
- **Chế độ ôm dáng (`FormSnapMode`) & Phân lớp (`LayerIndex`)**:
  - `Off`: Di chuyển tự do trên nhân vật.
  - `Hugging`: Ôm sát theo bề mặt cơ thể hoặc lớp vải bên dưới.
  - `Fitting`: Bắc cầu mượt qua các vùng lõm dựa trên `FittingBridgeStiffness` và giữ một phần nếp uốn đã điêu khắc (`PreserveDeformBlend`).
  - Hỗ trợ xếp nhiều lớp (`LayerIndex`) để tự động chống xuyên lớp và ẩn tam giác bị che khuất (`Occlusion Culling`).
- **Sub-Viewport (PiP)**: Hiển thị khung xem phụ trực tiếp giữa `Fabric` và `Character`.

### 3. Tối ưu hóa hiệu năng & Tiện ích
- **GPU Buffer Caching**: Quản lý `VAO`/`VBO`/`EBO` riêng theo từng mảnh vải, model nhân vật và tái sử dụng các primitive hình học dùng chung.
- **Topology & Scratch Cache**: `SubdivTopologyCache` tái sử dụng cấu trúc chia mịn Catmull-Clark và mảng tạm để hạn chế cấp phát bộ nhớ khi kéo chuột.
- **BVH Acceleration**: `CharacterBvh` tăng tốc Raycast và tìm điểm gần nhất trên lưới nhân vật/vải.
- **On-Demand Render Wakeup**: Giảm tải render khi giao diện nhàn rỗi thông qua `WakeUpRenderLoop`.
- **Lịch sử & I/O**: `Undo`/`Redo` (tối đa 50 bước), Copy/Paste vùng chọn, nhập/xuất `.obj` (hỗ trợ xuất theo tư thế đang ướm trên nhân vật).

## Phím tắt cơ bản

| Phím tắt | Chức năng |
| --- | --- |
| `Esc` | Chuyển về công cụ `Select` (hoặc hủy đường `Ruler` đang vẽ) |
| `A` | Công cụ `Create` (tạo điểm điều khiển / điểm neo) |
| `G` | Công cụ `Move` |
| `R` | Công cụ `Rotate` |
| `S` | Công cụ `Scale` |
| `M` | Công cụ `Ruler` (trong `CharacterViewport`) |
| `Enter` | Hoàn tất đường `Ruler` đang vẽ (trong `CharacterViewport`) |
| `F` | Nối các điểm hoặc cạnh đang chọn |
| `X` / `Delete` | Xóa điểm, cạnh, cạnh biên hoặc mảnh vải đang chọn |
| `Ctrl + C` / `Ctrl + V` | Sao chép / Dán đối tượng hoặc thành phần đang chọn |
| `Ctrl + Z` | Hoàn tác (`Undo`) |
| `Ctrl + Y` / `Ctrl + Shift + Z` | Làm lại (`Redo`) |

## Yêu cầu hệ thống

- Hệ điều hành: **Windows** (`net9.0-windows`)
- **.NET 9.0 SDK** trở lên
- GPU / driver hỗ trợ **OpenGL 3.3 Core Profile**

## Cách build và chạy

```powershell
# Restore & build toàn bộ solution
dotnet build CrimiradTailoring.sln

# Chạy ứng dụng Studio
dotnet run --project CrimiradTailoring.Studio
```

Tùy chọn dòng lệnh hỗ trợ:
- `--screenshot`: Khởi chạy ở chế độ chụp demo `FabricViewport`.
- `--screenshot-char`: Khởi chạy ở chế độ demo `CharacterViewport` (với nhiều lớp vải và thước đo mẫu).
