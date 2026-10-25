# Tích hợp chatbot điều hướng vào AHR+ (.NET 8 + Razor Pages)

Chatbot nhận câu hỏi tiếng Việt và trả **một đường dẫn** tới màn hình AHR+. Ví dụ, câu “Hôm nay tôi đã làm được bao nhiêu tiếng?” trả `/hr/attendance`; người dùng mở màn hình chấm công để xem số giờ thực tế. Chatbot không lấy số giờ, lương, điểm ABC, tài liệu hay dữ liệu nhân viên từ database hoặc API nghiệp vụ.

Các `/hr/...` trong repository là **đường dẫn minh họa**, chưa được đối chiếu với source code AHR+ của bạn. Trước khi tích hợp thật, đổi danh mục thành các đường dẫn Razor Pages đang tồn tại. Đường dẫn đích vẫn phải áp dụng phân quyền của AHR+.

## 1. Luồng tích hợp

```text
Razor Pages: giọng nói → văn bản có thể sửa → gửi yêu cầu
    ↓ POST cùng origin + cookie đăng nhập + antiforgery
Web/Chat handler: lấy permission claims của tài khoản đăng nhập
    ↓ POST /v1/chat bằng service token nội bộ
Chatbot API .NET 8: lọc danh mục theo quyền → chọn route hợp lệ
    ↓ text/plain, chỉ một đường dẫn
Widget: hiện đường dẫn để người dùng bấm mở
```

Service token chỉ xác thực kết nối Web → Chatbot API. Nó không thay thế đăng nhập của nhân viên. Không đưa service token vào JavaScript, HTML, localStorage hoặc mã nguồn được commit. Không cho trình duyệt tự gửi quyền tới chatbot API.

## 2. Chạy chatbot như dịch vụ nội bộ

Làm theo README để chạy API production với `Chatbot__ServiceToken` đã đặt trong môi trường server. Tạo token ngẫu nhiên đủ dài bằng trình quản lý bí mật hoặc công cụ hệ điều hành; dùng cùng token ở hai tiến trình API và Web. Không dùng `--demo` cho luồng tích hợp có người dùng thật.

Giữ API trên loopback hoặc mạng nội bộ. Khi Web và chatbot nằm ở hai máy khác nhau, dùng HTTPS và giới hạn truy cập mạng. Không cần mở CORS cho chatbot vì trình duyệt chỉ gọi Web AHR+.

Web cần các biến sau:

| Biến môi trường của Web | Ý nghĩa |
| --- | --- |
| `ChatbotProxy__BaseUrl` | Origin của chatbot API, mặc định `http://127.0.0.1:8787/`. Không kèm `/v1/chat`. |
| `ChatbotProxy__ServiceToken` | Cùng token bí mật đã cấu hình ở `Chatbot__ServiceToken` của chatbot API. |

Ví dụ cấu hình không có bí mật trong `appsettings.json` của Web:

```json
{
  "ChatbotProxy": {
    "BaseUrl": "http://127.0.0.1:8787/"
  }
}
```

Đặt `ChatbotProxy__ServiceToken` bằng secret store hoặc cấu hình môi trường của dịch vụ khi triển khai. Mẫu này kiểm tra độ dài tối thiểu 32 ký tự lúc khởi động. Không chép token vào tài liệu, lệnh demo hay file cấu hình đã commit.

## 3. Đưa adapter vào project Web đang có đăng nhập

Các file dùng được nằm trong [integration/aspnet](../integration/aspnet):

1. Chép `Services/ChatbotProxyOptions.cs`, `Services/AhrChatbotClient.cs` vào Web hoặc Infrastructure theo cách tổ chức hiện tại. Đổi namespace cho phù hợp solution AHR+.
2. Chép `Pages/Chat.cshtml` và `Pages/Chat.cshtml.cs` vào project Razor Pages Web. Mẫu trang này độc lập, có thể đưa phần widget vào `_Layout.cshtml` sau khi luồng `/Chat` chạy đúng.
3. Chép `public/widget.js`, `public/styles.css` vào `Web/wwwroot/chatbot/`; chép `integration/aspnet/wwwroot/integration.js` và `integration.css` vào `Web/wwwroot/`.
4. Giữ cấu hình đăng nhập đang có của AHR+. Thêm đăng ký options, typed client và antiforgery bên dưới vào `Program.cs` của Web.

```csharp
builder.Services.AddRazorPages(); // Dùng đăng ký hiện có nếu đã có.
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

builder.Services.AddOptions<ChatbotProxyOptions>()
    .BindConfiguration("ChatbotProxy")
    .Validate(o => o.IsValid(), "Chatbot proxy configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddHttpClient<AhrChatbotClient>((services, client) =>
    {
        var options = services
            .GetRequiredService<IOptions<ChatbotProxyOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(25);
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { AllowAutoRedirect = false });

// Giữ nguyên AddAuthentication/AddAuthorization và luồng login hiện có.
// Trong pipeline cần UseAuthentication(); UseAuthorization(); MapRazorPages();
```

Nếu AHR+ đã dùng tên header antiforgery khác, dùng tên đó cho cả cấu hình server và `integration.js`. Giữ antiforgery trên handler POST dùng cookie. Nếu login Web sử dụng JWT lưu trong session, principal của Web phải được tạo từ token đã xác minh bằng cơ chế đăng nhập hiện tại; không lấy role/permission từ header tùy ý của trình duyệt.

Mẫu project `Ahr.Chatbot.RazorIntegration.csproj` có thể build độc lập. Nó chỉ đăng ký cookie auth để thể hiện pipeline, **không có trang đăng nhập hoặc tài khoản giả**. Chạy riêng rồi mở `/Chat` khi chưa đăng nhập sẽ nhận HTTP 401; đây là hành vi dự kiến. Khi đưa vào AHR+, dùng cookie/principal hiện có của AHR+ thay vì thay thế cơ chế đăng nhập bằng mẫu này.

## 4. Ánh xạ quyền từ AHR+

`Chat.cshtml.cs` lấy các claim có type `permission` từ `User` đã xác thực. Request trình duyệt chỉ được có trường `message`; các trường như `permissions`, `employeeId` bị từ chối.

```json
{ "message": "Hôm nay tôi đã làm được bao nhiêu tiếng?" }
```

Web dựng request nội bộ, ví dụ sau khi tài khoản đã có claim `attendance:read:self`:

```json
{
  "message": "Hôm nay tôi đã làm được bao nhiêu tiếng?",
  "permissions": ["attendance:read:self"]
}
```

Các tên quyền trong `config/routes.example.json` là hợp đồng minh họa. Đổi chúng cho khớp quyền thực của AHR+, hoặc thêm hàm ánh xạ quyền ngay trên backend. Nếu hệ thống chỉ có role, tạo bảng ánh xạ **được kiểm soát phía server** từ role sang quyền cần thiết; không mặc định cấp mọi quyền cho mọi tài khoản. Route có nhiều quyền yêu cầu phải thỏa mãn tất cả.

API nhận tối đa 64 quyền trong mỗi request, theo định dạng chữ thường `a-z`, số và các ký tự `:._-` (tối đa 80 ký tự/tên). Nếu tài khoản AHR+ có nhiều quyền cho các module khác, Web cần ánh xạ/lọc còn các quyền liên quan đến danh mục chatbot trước khi gọi API; không chuyển nguyên mọi claim không liên quan hoặc cắt ngẫu nhiên danh sách quyền.

Chatbot chỉ lọc đường dẫn theo quyền. Chính màn hình/API nghiệp vụ đích phải kiểm tra rằng người dùng chỉ xem được nhân viên, kỳ chấm công hoặc phiếu lương mình được phép xem. Không cho model tự chọn employee ID hoặc tự thêm query string.

## 5. Thay danh mục điều hướng bằng đường dẫn thật

Mỗi mục có ID cố định, đường dẫn, nhãn/miêu tả giúp chọn màn hình, từ khóa và danh sách quyền. Ví dụ:

```json
{
  "id": "attendance",
  "label": "Chấm công của tôi",
  "description": "Màn hình xem chấm công và số giờ đã làm của tài khoản đăng nhập.",
  "path": "/hr/attendance",
  "keywords": ["giờ đã làm", "hôm nay làm bao nhiêu tiếng", "chấm công"],
  "permissions": ["attendance:read:self"]
}
```

`path` phải là đường dẫn nội bộ tĩnh: không domain ngoài, `//`, `..`, tham số động, query string hay fragment. Dùng đường dẫn trang Web, không dùng URL API trả dữ liệu. Nếu Razor Pages hiện đang dùng chữ hoa trong route thì giữ đúng route đó.

Thay `fallbackPath` bằng màn hình trợ giúp/điều hướng thật và bảo đảm có route công khai trong danh mục khớp đường dẫn này. Chatbot trả đường dẫn đó khi chưa hiểu, câu hỏi có nhiều ý không rõ, không có quyền hoặc model không chọn được mục hợp lệ. Nó không sinh thông tin nghiệp vụ để lấp chỗ trống.

Nhóm nghiệp vụ AHR+ được ưu tiên: chấm công/giờ làm, nội quy công ty, nghỉ phép, phiếu lương cá nhân, kết quả ABC hằng tháng và quy chế trừ điểm ABC. Danh mục không triển khai Sales KPI/Commission. Các câu về đi muộn và trừ điểm được điều hướng tới trang quy chế; chatbot không tự tính điểm ABC.

Nếu dùng routing của Razor Pages có base path, đưa base path đó vào `path` của danh mục. Widget cần gọi đúng cùng-origin endpoint của Web; `@Url.Page("/Chat", "Send")` trong mẫu đã sinh endpoint handler theo routing của Web.

## 6. Đặt widget vào layout Razor

Trang `Chat.cshtml` đã chứa đầy đủ phần khởi tạo. Khi chuyển vào `_Layout.cshtml`, giữ ba phần:

```cshtml
<form id="chat-antiforgery" hidden>@Html.AntiForgeryToken()</form>
<div id="ahr-chatbot" data-endpoint="@Url.Page("/Chat", "Send")"></div>
<script type="module" src="~/integration.js"></script>
```

Nạp stylesheet của widget trong `<head>`. Mỗi trang chỉ tạo một widget/token form. Module đọc hidden antiforgery token và gửi bằng header `RequestVerificationToken`; tuyệt đối không thay bằng service token.

Thu giọng nói dùng Web Speech API của trình duyệt, mặc định `vi-VN`. Cần HTTPS hoặc localhost và quyền microphone; một số trình duyệt không hỗ trợ hoặc cần dịch vụ mạng của nhà cung cấp trình duyệt. Âm thanh không được gửi tới API chatbot trong repository này, nhưng dịch vụ nhận giọng nói của trình duyệt có thể xử lý âm thanh từ xa. Người dùng luôn có thể gõ, sửa bản chép và bấm gửi. Không tự gửi ngay sau khi nói.

## 7. Ollama và Clean Architecture

Ollama là tùy chọn để chọn route theo cách diễn đạt đa dạng. Nó chỉ nhận câu hỏi và danh mục màn hình đã lọc theo quyền, không có kết nối database, không gọi API chấm công và không được tự tạo URL. Không cần train lại model, embedding hoặc RAG cho chức năng điều hướng này. Xem README để bật chế độ Ollama; chất lượng tiếng Việt cần thử trên câu hỏi thật của công ty trước khi triển khai.

Nếu muốn đưa hoàn toàn vào solution AHR+ thay vì chạy dịch vụ riêng, phân chia như sau:

| Project AHR+ | Phần tích hợp |
| --- | --- |
| Domain | Giữ quy tắc nghiệp vụ hiện có; chatbot không cần thêm truy cập dữ liệu. |
| Application | Interface nhận câu hỏi + quyền và trả đường dẫn; điều phối resolver. |
| Infrastructure | Loader danh mục route, adapter HTTP Ollama. |
| API | Endpoint được bảo vệ, xác định quyền từ principal, validation/rate limit. |
| Web | Widget, speech-to-text, antiforgery và hiển thị đường dẫn. |

`Ahr.Chatbot.Core` tách khỏi ASP.NET và EF Core để có thể tái sử dụng; `Ahr.Chatbot.Infrastructure` chỉ phụ thuộc Core và HTTP tới Ollama. Với cách chạy riêng trong hướng dẫn này, typed client HTTP ở Web đã đủ, không cần ghép mọi project vào solution AHR+.

## 8. Kiểm tra trước khi đưa vào AHR+

- Chưa đăng nhập: Web không gọi chatbot, trả 401/redirect login theo auth hiện có.
- Thiếu antiforgery token: handler POST từ chối request.
- Người dùng thường hỏi nhân sự/lương người khác: chỉ nhận đường dẫn được phép hoặc đường dẫn trợ giúp; không có dữ liệu cá nhân trong response.
- “Hôm nay tôi đã làm bao nhiêu giờ?”: response đúng một đường dẫn tới chấm công, không có số giờ.
- “Đi muộn trừ điểm ABC thế nào?”: mở quy chế ABC; “Xem điểm ABC tháng này”: mở trang kết quả ABC khi có quyền.
- Nhập văn bản chứa HTML hoặc yêu cầu bỏ qua quy tắc: giao diện hiển thị an toàn, response vẫn là route trong danh mục.
- Kiểm tra riêng microphone trên trình duyệt/thiết bị đích; unit test không thay thế thử quyền microphone thật.
- Thay hết đường dẫn minh họa và thử mở từng màn hình bằng tài khoản thật có/không có quyền.

Không cần cung cấp connection string cho chatbot. Việc màn hình chấm công/nội quy của AHR+ hiện lấy dữ liệu thật hay `MockStore` thuộc màn hình đích; chatbot không biến dữ liệu demo thành dữ liệu thật.
