import { mountHRChatbot } from './widget.js';

mountHRChatbot(document.getElementById('hr-chatbot'), {
  endpoint: '/demo/chat',
  title: 'Trợ lý điều hướng AHR',
  suggestions: [
    'Hôm nay tôi đã làm bao nhiêu giờ?',
    'Xem nội quy công ty',
    'Đi muộn trừ điểm ABC thế nào?',
    'Xem điểm ABC tháng này',
  ],
});
