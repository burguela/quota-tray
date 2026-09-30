import Foundation

/// Bounds a provider's optional local-log scan so its live limits still publish when the history is slow
/// to read. The first scan of a large history (or a slow disk) can outlast the whole provider deadline,
/// and a timed-out refresh publishes nothing at all, not even the limits the API already returned. Past
/// the budget the scan is cancelled; the files it finished stay in the parse cache, so the next refresh
/// resumes it, and the store keeps the last good history meanwhile.
enum LocalUsageScanBudget {
    /// Half the provider deadline (`WidgetDataStore.providerRefreshTimeout`, 120s), which leaves the rest
    /// for the live requests and pricing.
    static let standard: Duration = .seconds(60)

    private enum Outcome<Value: Sendable>: Sendable {
        case finished(Value)
        case overBudget
        case cancelled
    }

    /// One-shot hand-off between the scan, the budget timer and the caller's own cancellation: the first
    /// outcome wins and resumes the caller, and the other tasks are then cancelled but never awaited.
    private final class Race<Value: Sendable>: @unchecked Sendable {
        private let lock = NSLock()
        private var resolved: Outcome<Value>?
        private var continuation: CheckedContinuation<Outcome<Value>, Never>?
        private var tasks: [Task<Void, Never>] = []

        func install(_ continuation: CheckedContinuation<Outcome<Value>, Never>) {
            lock.lock()
            if let resolved {
                lock.unlock()
                continuation.resume(returning: resolved)
            } else {
                self.continuation = continuation
                lock.unlock()
            }
        }

        func adopt(_ task: Task<Void, Never>) {
            lock.lock()
            let alreadyResolved = resolved != nil
            if !alreadyResolved { tasks.append(task) }
            lock.unlock()
            if alreadyResolved { task.cancel() }
        }

        func finish(_ outcome: Outcome<Value>) {
            lock.lock()
            guard resolved == nil else {
                lock.unlock()
                return
            }
            resolved = outcome
            let continuation = self.continuation
            self.continuation = nil
            let tasks = self.tasks
            self.tasks = []
            lock.unlock()
            continuation?.resume(returning: outcome)
            for task in tasks { task.cancel() }
        }
    }

    /// The scan's result, or `nil` when `budget` elapsed first (or the caller was cancelled). Returns as
    /// soon as the budget elapses, even if the scan is stuck in work that never checks for cancellation
    /// (a slow directory walk, a blocking read): a structured task group would wait for that child and
    /// let the refresh run into the provider's hard deadline, which publishes nothing at all. The
    /// cancelled scan winds down in the background and its finished files stay in the parse cache.
    static func run<Value: Sendable>(
        budget: Duration,
        providerID: String,
        _ scan: @escaping @Sendable () async -> Value
    ) async -> Value? {
        let race = Race<Value>()
        let outcome = await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Outcome<Value>, Never>) in
                race.install(continuation)
                race.adopt(Task { race.finish(.finished(await scan())) })
                race.adopt(Task {
                    try? await Task.sleep(for: budget)
                    guard !Task.isCancelled else { return }
                    race.finish(.overBudget)
                })
            }
        } onCancel: {
            race.finish(.cancelled)
        }
        switch outcome {
        case let .finished(value):
            return value
        case .overBudget:
            if !Task.isCancelled {
                AppLog.warn(
                    LogTag.plugin(providerID),
                    "local usage history not read within \(budget); showing live limits now, "
                        + "the history scan resumes on the next refresh"
                )
            }
            return nil
        case .cancelled:
            return nil
        }
    }
}
