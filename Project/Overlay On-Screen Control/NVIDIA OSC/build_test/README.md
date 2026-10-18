# NVIDIA OSC Test Build

## วิธี Build

เปิด PowerShell แล้วรัน:

```powershell
& "C:\My Project\NVIDIA-Shadowplay\Project\Overlay On-Screen Control\NVIDIA OSC\build_test\build-osc-test.ps1"
```

สคริปต์ใช้ `NVIDIA OSC.vcxproj` เดียวกับตัวหลัก แต่ override output ให้แยก:

- EXE: `build_test/NVIDIA OSC_Test/NVIDIA OSC_Test.exe`
- Intermediate: `build_test/obj/`

## ขอบเขตที่ต้องเข้าใจ

- สคริปต์ **Build เท่านั้น**: ไม่คัดลอกไปที่ `Build/NVIDIA ShadowPlay`, ไม่แทนที่ Live EXE และไม่เปิดโปรแกรมให้อัตโนมัติ
- EXE ที่ได้เป็น build artifact แยก ไม่ใช่ runtime package ที่มี CEF DLL/resource ครบถ้วน จึงอย่าเข้าใจว่า Build ผ่านแล้วหมายถึงพร้อมเปิดทดสอบทุกอย่าง
- โค้ด Host ปัจจุบันยังค้นหา Config/UI ตามโครงสร้าง `Project`/runtime ที่มีอยู่ การเปิด test EXE อาจแตะ Config หรือ Cache ที่ใช้ร่วมกัน จึงยังไม่ควรเปิดพร้อมกับ Live/Hook test จนกว่าจะทำ runtime sandbox แยกโดยเฉพาะ
- โฟลเดอร์ output/intermediate ถูก ignore จาก Git; Source ของสคริปต์และ README ยังคงเก็บใน Git ได้
