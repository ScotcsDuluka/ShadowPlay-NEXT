# D3D11 Frame Publisher

โมดูลนี้เป็น implementation ที่ Build ใช้จริงสำหรับ D3D11 frame bridge ของ OSC

- เลือก consumer adapter จาก shared consumer-adapter mapping และเปิด D3D11 device เมื่อจำเป็น
- สร้าง named NT shared texture พร้อม keyed mutex และ metadata bridge ตาม protocol เดิม
- จัดการ cross-adapter staging/readback ring กับ publish worker
- เชื่อม Host ผ่าน HostBindings; จุดเข้าใช้งานที่ต้องล็อกใช้ Mutex() เดียวกันกับ Desktop/CEF rendering
- รองรับการส่งภาพ CPU BGRA ไป D3D9 backend เมื่อ consumer API เป็น D3D9

D3D10 ยังไม่มี implementation ใน bridge ปัจจุบัน
