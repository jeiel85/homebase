package io.github.jeiel85.homebase.ui

import org.junit.Assert.assertEquals
import org.junit.Test

class HardwarePanelTest {
    @Test
    fun gpu_names_drop_the_vendor_and_trademark_marks() {
        assertEquals("RTX 5060 Laptop", shortGpuName("NVIDIA GeForce RTX 5060 Laptop GPU"))
        assertEquals("Radeon 780M Graphics", shortGpuName("AMD Radeon 780M Graphics"))
        assertEquals("Radeon Graphics", shortGpuName("AMD Radeon(TM) Graphics"))
        assertEquals("UHD Graphics 770", shortGpuName("Intel(R) UHD Graphics 770"))
        assertEquals("NVIDIA", shortGpuName("NVIDIA")) // nothing left: keep the name as it is
    }
}
