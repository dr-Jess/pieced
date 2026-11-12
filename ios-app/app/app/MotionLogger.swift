//
//  MotionLogger.swift
//  app
//
//  Created by Jesse Zong on 10/19/26.
//

import CoreMotion
import Combine

/// One IMU reading, encoded as a "motion" message on the wire (tools/stream/README.md).
nonisolated struct MotionSample: Encodable, Identifiable, Sendable {
    var type = "motion"
    let seq: Int
    let t: TimeInterval            // seconds since device boot
    let ax, ay, az: Double         // user acceleration (gravity removed), g
    let gx, gy, gz: Double         // rotation rate, rad/s
    let qx, qy, qz, qw: Double     // attitude quaternion
    let grx, gry, grz: Double      // gravity, g, device frame

    var id: Int { seq }
    var accelerationMagnitude: Double { (ax * ax + ay * ay + az * az).squareRoot() }

    private enum CodingKeys: String, CodingKey {
        case type, seq, t, ax, ay, az, gx, gy, gz, qx, qy, qz, qw, grx, gry, grz
    }
}

/// Numbers samples and forwards them to the stream. Only touched from the CoreMotion queue.
nonisolated private final class SamplePump: @unchecked Sendable {
    private var seq = 0
    private let client: StreamClient

    init(client: StreamClient) { self.client = client }

    func process(_ m: CMDeviceMotion) -> MotionSample {
        seq += 1
        let a = m.userAcceleration, r = m.rotationRate, q = m.attitude.quaternion, g = m.gravity
        let sample = MotionSample(seq: seq, t: m.timestamp,
                                  ax: a.x, ay: a.y, az: a.z,
                                  gx: r.x, gy: r.y, gz: r.z,
                                  qx: q.x, qy: q.y, qz: q.z, qw: q.w,
                                  grx: g.x, gry: g.y, grz: g.z)
        client.send(sample)
        return sample
    }
}

final class MotionManager: ObservableObject {
    static let sampleRate = 100.0
    private static let uiEveryNthSample = 5   // refresh the UI at 20 Hz; streaming stays at full rate

    let stream: StreamClient
    private let motion = CMMotionManager()
    private let queue: OperationQueue = {
        let q = OperationQueue()
        q.maxConcurrentOperationCount = 1
        q.qualityOfService = .userInteractive
        return q
    }()

    @Published var latest: MotionSample?
    @Published var log: [MotionSample] = []
    @Published var isRunning = false

    var isAvailable: Bool { motion.isDeviceMotionAvailable }

    init(stream: StreamClient) { self.stream = stream }

    func start() {
        guard motion.isDeviceMotionAvailable, !motion.isDeviceMotionActive else { return }
        motion.deviceMotionUpdateInterval = 1.0 / Self.sampleRate
        let pump = SamplePump(client: stream)
        let uiEvery = Self.uiEveryNthSample
        let show: @MainActor @Sendable (MotionSample) -> Void = { [weak self] sample in self?.show(sample) }

        motion.startDeviceMotionUpdates(using: .xArbitraryZVertical, to: queue) { @Sendable data, _ in
            guard let data else { return }
            let sample = pump.process(data)
            guard sample.seq % uiEvery == 0 else { return }
            Task { @MainActor in show(sample) }
        }
        isRunning = true
    }

    func stop() {
        motion.stopDeviceMotionUpdates()
        isRunning = false
    }

    private func show(_ sample: MotionSample) {
        latest = sample
        log.append(sample)
        if log.count > 100 {
            log.removeFirst(log.count - 100)
        }
    }
}
