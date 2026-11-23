import { Suspense } from "react";
import { DeviceDetail } from "@/features/devices/device-detail";

export const metadata = { title: "Device" };

export default function Page() {
  return (
    <Suspense>
      <DeviceDetail />
    </Suspense>
  );
}
