# AHR+ — chatbot điều hướng bằng văn bản và giọng nói

Chatbot cho hệ thống nhân sự AHR+ của Công ty TNHH Dược phẩm Đức Anh, dùng **C# .NET 8** và widget có thể gắn vào **Razor Pages**. Người dùng nói hoặc gõ, sửa bản chép rồi bấm gửi. Phản hồi thành công là **đúng một đường dẫn nội bộ**, không có câu trả lời chứa dữ liệu nhân sự.

| Người dùng hỏi | Phản hồi minh họa |
| --- | --- |
| Hôm nay tôi đã làm bao nhiêu giờ? | `/hr/attendance` |
| Xem nội quy công ty | `/hr/policies` |
| Đi muộn có bị trừ điểm ABC không? | `/hr/evaluations/abc/rules` |
| Xem điểm ABC tháng này | `/hr/evaluations/abc` |
| Tôi muốn xin nghỉ phép | `/hr/leave/new` |

**Các `/hr/...` là đường dẫn mẫu, chưa phải routing thật của AHR+.** Chatbot không truy vấn database, không đọc API nghiệp vụ, không tính giờ công/điểm ABC và không tạo đơn nghỉ phép. Người dùng mở trang đích để xem dữ liệu hoặc tự xác nhận thao tác. Không có Sales KPI/Commission trong phạm vi.

## Chạy thử

Yêu cầu: .NET SDK **8.0.426** (hoặc bản vá tương thích theo `global.json`). Node.js 22+ chỉ cần khi chạy kiểm thử widget. Không cần database, API key hay model để thử chế độ `rules`.

Trong môi trường cloud này:

```bash
cd /workspace/chatbot-ahr
bash scripts/setup.sh
bash scripts/run-demo.sh
```

`setup.sh` cài SDK Linux x64 nếu thiếu, xác minh SHA-512 từ bản phát hành chính thức rồi restore bằng lockfile và build solution. `scripts/env.sh` khởi tạo SDK/cache/runtime trong `/workspace`, kể cả cấu hình XDG cần cho filesystem chỉ đọc ở ngoài workspace.

Trên máy đã cài .NET SDK, có thể dùng trực tiếp:

```bash
dotnet restore Ahr.Chatbot.sln --locked-mode
dotnet build Ahr.Chatbot.sln --no-restore
dotnet run --project src/Ahr.Chatbot.Api --no-build --no-launch-profile -- --demo
```

Demo chỉ lắng nghe loopback, cổng mặc định `8787`; mở trang gốc của dịch vụ trên máy bạn để thử. Trong Codex cloud, dùng request nội bộ để kiểm tra vì onboarding không cung cấp web preview. Demo dùng quyền nhân viên mẫu, không đăng nhập vào AHR+ và không mở được các trang `/hr/...` khi AHR+ chưa được ghép vào.

```bash
curl --fail http://127.0.0.1:8787/healthz
curl --fail http://127.0.0.1:8787/demo/chat \
  -H 'Content-Type: application/json' \
  --data '{"message":"Hôm nay tôi đã làm bao nhiêu giờ?"}'
```

Request thứ hai phải trả `/hr/attendance`, không trả số giờ. Thu giọng nói dùng Web Speech API `vi-VN`: cần trình duyệt hỗ trợ, HTTPS/localhost và quyền micro. Trình duyệt có thể xử lý âm thanh qua dịch vụ của nhà cung cấp; API chatbot này chỉ nhận văn bản. Luôn có thể gõ khi micro không dùng được.

Trong Visual Studio, đặt **Ahr.Chatbot.Api** làm Startup Project, chọn launch profile **Demo**, rồi F5. `Ahr.Chatbot.Core` là thư viện, không chạy trực tiếp. Profile Demo đã có `--demo` và môi trường Development.

Nếu cập nhật từ bản cũ gặp `DirectoryNotFoundException` trỏ tới `src/Ahr.Chatbot.Api/wwwroot`, dừng debug rồi chạy `dotnet clean Ahr.Chatbot.sln` và build lại để xóa manifest static assets cũ. Giao diện nằm trong `public/` và được chép sang `bin/.../wwwroot` khi build; không cần tự tạo thư mục `wwwroot` trong project API.

## API dùng khi tích hợp

Chạy dịch vụ **không có `--demo`** và cấu hình bí mật `Chatbot__ServiceToken` ở phía server. Token gồm 32–512 ký tự chữ/số ASCII hoặc `_-.~` (không có khoảng trắng). Không đưa token vào frontend hay commit vào Git. `.env.example` chỉ liệt kê tên cấu hình; .NET không tự nạp file `.env` này.

```bash
dotnet run --project src/Ahr.Chatbot.Api --no-build --no-launch-profile
```

Backend Web đã xác thực người dùng gọi:

```http
POST /v1/chat
Authorization: Bearer <service-token-from-server-secret-store>
Content-Type: application/json

{"message":"Hôm nay tôi đã làm bao nhiêu giờ?","permissions":["attendance:read:self"]}
```

```http
HTTP/1.1 200 OK
Content-Type: text/plain; charset=utf-8
Cache-Control: no-store

/hr/attendance
```

`permissions` do backend lấy từ principal/claims đã xác thực; không nhận quyền trực tiếp từ trình duyệt. Mỗi route yêu cầu đủ toàn bộ quyền đã khai báo. Không rõ yêu cầu, nhiều ý hoặc không đủ quyền thì trả `fallbackPath` công khai (`/hr/help` trong mẫu). Lỗi giao thức dùng HTTP 400/401/413/415/429/503, không được hiển thị thành câu trả lời của chatbot.

Dịch vụ mặc định bind `127.0.0.1:8787`, không mở CORS. Giới hạn mặc định 60 request/phút/IP; với mô hình Web proxy đây là IP của Web. Điều chỉnh `Chatbot__RateLimitPerMinute` theo tải và dùng giới hạn theo tài khoản ở backend AHR+ nếu cần.

## Danh mục đường dẫn và Ollama

Chép `config/routes.example.json` thành `config/routes.local.json`, thay các `path` bằng route Razor thật và `permissions` bằng quyền AHR+ đang dùng. Cấu hình `Chatbot__RouteCatalog` bằng **đường dẫn tuyệt đối** tới file mới. Đường dẫn cấu hình tương đối được tính từ thư mục chứa DLL chạy, không từ thư mục shell.

Catalog chỉ chấp nhận đường dẫn nội bộ tĩnh tối đa 200 ký tự; không domain, query, fragment, `..`, `//`, mã hóa `%` hay ID nhân viên do model sinh. `fallbackPath` phải có mục tương ứng với `permissions: []`. Thay đổi catalog cần khởi động lại dịch vụ. Quyền ở trang/API đích vẫn là lớp kiểm tra bắt buộc của AHR+.

Mặc định `Chatbot__Resolver=rules` dùng từ khóa tiếng Việt có/không dấu, không cần tải model. Chế độ `ollama` dùng các quy tắc đã biết trước, rồi nhờ model chọn mã màn hình cho cách diễn đạt chưa khớp. Model chỉ nhận câu hỏi và metadata các màn hình được phép; URL được lấy từ catalog sau khi kiểm tra lại mã trả về.

Nếu máy bạn đã cài Ollama, có thể bắt đầu với model nhỏ hỗ trợ tiếng Việt và structured output:

```bash
ollama pull qwen2.5:3b
ollama serve
```

Không chạy `ollama serve` lần nữa nếu dịch vụ đã chạy. Đặt các biến môi trường sau cho API rồi khởi động lại:

```text
Chatbot__Resolver=ollama
Ollama__BaseUrl=http://127.0.0.1:11434/
Ollama__Model=qwen2.5:3b
Ollama__TimeoutSeconds=10
```

Có thể đổi model thành tag đã cài trên Ollama; không cần train, embedding hoặc RAG cho chức năng điều hướng. HTTP chỉ dùng với loopback; máy chủ model từ xa phải có HTTPS. Không tự theo HTTP redirect. Khi model lỗi, quá hạn hoặc chọn sai mã/quyền, câu chưa khớp sẽ về link trợ giúp, không sinh nội dung thay thế. Adapter được kiểm thử bằng HTTP giả lập; repository không kèm model và chưa benchmark model thật trên câu hỏi công ty.

## Tích hợp .NET 8 / Razor Pages

Xem hướng dẫn đầy đủ tại **[docs/INTEGRATION.vi.md](docs/INTEGRATION.vi.md)** và project build được **[integration/aspnet](integration/aspnet)**.

1. Chạy Chatbot API nội bộ với `Chatbot__ServiceToken`.
2. Đưa typed client, Razor Page `/Chat`, JS/CSS từ ví dụ vào project Web đang có đăng nhập. Giữ auth hiện có và antiforgery.
3. Web đặt `ChatbotProxy__BaseUrl` và `ChatbotProxy__ServiceToken`; token chỉ ở server. Web lấy claim `permission`, còn trình duyệt chỉ gửi `{ "message": "..." }`.
4. Thay catalog bằng URL thật, ánh xạ quyền, kiểm tra bằng tài khoản thật. Chưa cần thêm connection string hoặc quyền truy cập database cho chatbot.

Core tách ASP.NET/EF để tái sử dụng trong Clean Architecture. Có thể ghép Core/Infrastructure vào solution AHR+ hoặc chạy API này như dịch vụ riêng. Ví dụ Razor độc lập không tạo login giả: `/Chat` trả 401 khi chưa ghép cơ chế đăng nhập AHR+.

## Kiểm thử và xuất bản chạy VPS

```bash
bash scripts/check.sh
```

Hoặc khi đã kích hoạt SDK trên máy:

```bash
dotnet test tests/Ahr.Chatbot.Tests --no-restore
node --test test/*.test.mjs
dotnet publish src/Ahr.Chatbot.Api -c Release -o artifacts/api
```

Kiểm thử bao gồm phân quyền, tiếng Việt, các câu hỏi ABC/giờ công, URL sai, yêu cầu vượt phạm vi, API Kestrel thật, adapter Razor và Ollama giả lập. Kiểm thử speech dùng sự kiện trình duyệt giả lập; vẫn phải thử micro thật trên thiết bị đích.

Trên VPS, chạy DLL từ `artifacts/api` bằng ASP.NET Core Runtime 8 đã cập nhật, cấu hình catalog/token qua môi trường dịch vụ và để AHR+ Web gọi nội bộ. Nếu đi qua mạng giữa các máy, cấu hình HTTPS. Không bật demo ở môi trường có người dùng thật. Publish ứng dụng và publish snapshot môi trường Codex là hai thao tác riêng; repository này không tự deploy lên VPS.
