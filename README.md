# Crimirad Cloth Sculpt Editor

Ứng dụng desktop dựng và điêu khắc mô hình vải 3D (**CrimiradTailoring**), xây dựng bằng C# / .NET với OpenGL 3.3 (`OpenTK`) và giao diện `ImGui.NET`.

## Cấu trúc dự án

- `CrimiradTailoring.sln` — Solution chính của dự án.
- `CrimiradTailoring.Core/` (`netstandard2.1`) — Thư viện lõi xử lý hình học lưới tứ giác (quad mesh), chọn vùng, cắt mảnh, biến dạng theo điểm/cạnh điều khiển, ràng buộc liên kết và nhập/xuất `.obj`:
  - `Engine/ClothSculptEngine.cs`
  - `Models/ClothSculptModels.cs`, `Models/Primitives.cs`
- `CrimiradTailoring.Studio/` (`net9.0-windows`) — Ứng dụng desktop OpenGL + ImGui:
  - `Program.cs`, `StudioWindow.cs`
  - `Rendering/GlClothViewportRenderer.cs`, `Rendering/ImGuiController.cs`
  - `Platform/NativeFileDialog.cs`

## Tính năng chính

- **Khởi tạo & hiển thị lưới vải 3D**: Tạo tấm vải lưới tứ giác (mặc định `24x24`), hỗ trợ các chế độ shading (`Smooth`, `AutoSmooth`, `Flat`), wireframe overlay, lưới sàn và gizmo điều hướng camera 6 trục.
- **Chọn vùng & thành phần (`Select`)**: Chọn vùng theo hình Vuông (`Box`), Tròn (`Circle`), Nón (`Cone`), hoặc chọn trực tiếp mảnh vải (`ClothObject`), điểm điều khiển (`Point`) và cạnh điều khiển (`Edge`).
- **Cắt vải (`Cut`)**: Tách vùng đã chọn thành mảnh mới (`ExtractPiece`) hoặc khoét lỗ (`RemoveHole`).
- **Điêu khắc bằng Điểm & Cạnh điều khiển**:
  - Tạo `ControlPoint` trên bề mặt vải để uốn vùng hình cầu quanh điểm.
  - Nối 2 điểm thành `ControlEdge` để uốn vùng hình trụ theo trục cạnh với tùy chỉnh bán kính (`Radius`) và độ uốn (`Bending`).
- **Biến đổi (`Move`, `Rotate`, `Scale`)**: Di chuyển, xoay theo trục hoặc tự do, và thay đổi tỉ lệ mảnh vải.
- **Liên kết (`Connect`)**: Tạo cạnh điều khiển (`CreateEdge`), dính khít (`Snap`), hoặc treo theo khoảng cách (`Hang`) giữa các điểm/cạnh.
- **Lịch sử thao tác & I/O**: Hoàn tác/Làm lại (`Undo`/`Redo` tối đa 50 bước) và nhập/xuất mô hình định dạng Wavefront `.obj`.

## Phím tắt cơ bản

| Phím tắt | Chức năng |
| --- | --- |
| `Esc` | Chuyển về công cụ `Select` |
| `A` | Công cụ `Create` (tạo điểm điều khiển trên mặt vải) |
| `G` | Công cụ `Move` (giữ `Shift` để kéo theo chiều sâu Z) |
| `R` | Công cụ `Rotate` |
| `S` | Công cụ `Scale` |
| `F` | Công cụ `Connect` (hoặc nối nhanh khi đang chọn nhiều điểm/cạnh) |
| `X` / `Delete` | Xóa điểm, cạnh hoặc mảnh vải đang chọn |
| `Ctrl + Z` | Hoàn tác (`Undo`) |
| `Ctrl + Y` / `Ctrl + Shift + Z` | Làm lại (`Redo`) |

## Yêu cầu hệ thống

- Hệ điều hành: **Windows** (do `CrimiradTailoring.Studio` target `net9.0-windows`)
- **.NET 9.0 SDK** trở lên
- GPU / driver hỗ trợ **OpenGL 3.3 Core Profile**

## Cách build và chạy

```powershell
# Restore & build toàn bộ solution
dotnet build CrimiradTailoring.sln

# Chạy ứng dụng Studio
dotnet run --project CrimiradTailoring.Studio
```
