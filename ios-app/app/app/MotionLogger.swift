//
//  MotionLogger.swift
//  app
//
//  Created by Jesse Zong on 10/19/26.
//

import CoreMotion
import Combine

struct AccelSample: Identifiable {
    let id = UUID()
    let timestamp: TimeInterval   // seconds since device boot
    let x: Double                 // in g
    let y: Double
    let z: Double
}

final class MotionManager: ObservableObject {
    private let motion = CMMotionManager()
    private let queue = OperationQueue()

    @Published var latest: AccelSample?
    @Published var log: [AccelSample] = []
    @Published var isRunning = false

    var isAvailable: Bool { motion.isAccelerometerAvailable }

    func start(hz: Double = 50) {
        guard motion.isAccelerometerAvailable, !motion.isAccelerometerActive else { return }
        motion.accelerometerUpdateInterval = 1.0 / hz

        motion.startAccelerometerUpdates(to: queue) { [weak self] data, error in
            guard let self, let d = data else { return }
            let sample = AccelSample(timestamp: d.timestamp,
                                     x: d.acceleration.x,
                                     y: d.acceleration.y,
                                     z: d.acceleration.z)
            DispatchQueue.main.async {
                self.latest = sample
                self.log.append(sample)
                if self.log.count > 100 {
                    self.log.removeFirst(self.log.count - 100)
                }
            }
        }
        isRunning = true
    }

    func stop() {
        motion.stopAccelerometerUpdates()
        isRunning = false
    }
}
