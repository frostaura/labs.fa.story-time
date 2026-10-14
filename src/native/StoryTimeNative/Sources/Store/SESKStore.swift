import Foundation
import StoreKit
import UIKit

/// Objective-C facing StoreKit 2 shim for StoryTime's one product: a single non-consumable
/// bought once on the device (no subscription, no account, no server). Consumed by the .NET
/// binding (src/native/StoryTimeNative.Binding) behind Core's `IPremiumStore` seam; the C#
/// side (StoryTime.Core Services/StoreKitPremiumStore.cs) owns the local ledger.
///
/// - Completion handlers, never `async`; every handler runs on the MAIN queue.
/// - Outcomes are plain strings that match Core's `PremiumPurchaseResult` names:
///   purchased | restored | cancelled | pending | failed | nothingToRestore |
///   storeUnavailable | notFound | purchasesDisabled.
/// - Every transaction is checked with StoreKit's JWS verification. Unverified transactions
///   never grant Premium and are left unfinished (as Apple's sample does).
/// - `start` runs the `Transaction.updates` listener for the app's lifetime (Ask to Buy
///   approvals, purchases from other devices, refunds and revocations) and reconciles the
///   latest transaction at launch, so a refund made while the app was closed is seen.
@objc(SESKStore)
public final class SESKStore: NSObject, @unchecked Sendable {
    @objc public static let shared = SESKStore()

    public typealias ProductHandler = @Sendable (String) -> Void
    public typealias OutcomeHandler = @Sendable (String, String?) -> Void
    public typealias EntitlementHandler = @Sendable (String, Bool) -> Void

    // Guarded by `lock`.
    private let lock = NSLock()
    private var listener: Task<Void, Never>?
    private var products: [String: Product] = [:]

    override private init() {
        super.init()
    }

    /// In-app purchases are allowed on this device (false under Screen Time restrictions).
    @objc public var canMakePayments: Bool { AppStore.canMakePayments }

    // MARK: - Transaction listener

    /// Starts the `Transaction.updates` listener (idempotent) and reports the launch-time
    /// state of `productId`. `onEntitlement(productId, owned)` fires for every verified
    /// change: owned = true for a live purchase, false for a refund or revocation.
    @objc(startWithProductId:onEntitlement:)
    public func start(productId: String, onEntitlement: @escaping EntitlementHandler) {
        lock.lock()
        defer { lock.unlock() }
        guard listener == nil else { return }
        listener = Task.detached { [weak self] in
            // Launch reconcile first: an owned entitlement (bought on another device, or a
            // purchase whose record was lost) grants; a revoked latest transaction revokes.
            if let owned = await Self.launchState(productId: productId) {
                Self.main { onEntitlement(productId, owned) }
            }
            for await update in Transaction.updates {
                guard self != nil else { return }
                guard case .verified(let transaction) = update else { continue }
                if transaction.productID == productId {
                    let owned = transaction.revocationDate == nil
                    Self.main { onEntitlement(productId, owned) }
                }
                await transaction.finish()
            }
        }
    }

    /// true: owned now; false: the latest transaction was revoked (refund); nil: nothing to say.
    private static func launchState(productId: String) async -> Bool? {
        for await entitlement in Transaction.currentEntitlements(for: productId) {
            if case .verified(let transaction) = entitlement, transaction.revocationDate == nil {
                return true
            }
        }
        if let latest = await Transaction.latest(for: productId),
           case .verified(let transaction) = latest, transaction.revocationDate != nil {
            return false
        }
        return nil
    }

    // MARK: - Product

    /// Result JSON `{"state": "available"|"notFound"|"storeUnavailable"|"purchasesDisabled",
    /// "displayPrice": String?, "displayName": String?}`.
    @objc(loadProduct:completion:)
    public func loadProduct(productId: String, completion: @escaping ProductHandler) {
        guard AppStore.canMakePayments else {
            Self.main { completion(Self.json(["state": "purchasesDisabled"])) }
            return
        }
        Task { [weak self] in
            do {
                let found = try await Product.products(for: [productId])
                guard let product = found.first(where: { $0.id == productId }) else {
                    Self.main { completion(Self.json(["state": "notFound"])) }
                    return
                }
                self?.remember(product)
                let payload = Self.json(["state": "available", "displayPrice": product.displayPrice,
                                         "displayName": product.displayName])
                Self.main { completion(payload) }
            } catch {
                Self.main { completion(Self.json(["state": "storeUnavailable", "message": String(describing: error)])) }
            }
        }
    }

    // MARK: - Purchase

    /// Buys `productId` (confirmation sheet shown in the foreground window scene).
    @objc(purchase:completion:)
    public func purchase(productId: String, completion: @escaping OutcomeHandler) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            guard AppStore.canMakePayments else { return completion("purchasesDisabled", nil) }
            do {
                guard let product = try await self.product(productId) else { return completion("notFound", nil) }
                guard let scene = Self.foregroundScene() else { return completion("failed", "no foreground window scene") }
                let result = try await product.purchase(confirmIn: scene)
                switch result {
                case .success(let verification):
                    switch verification {
                    case .verified(let transaction):
                        await transaction.finish()
                        completion(transaction.revocationDate == nil ? "purchased" : "failed",
                                   transaction.revocationDate == nil ? nil : "revoked")
                    case .unverified(_, let reason):
                        completion("failed", "verification: \(reason)")
                    }
                case .pending:
                    completion("pending", nil)   // Ask to Buy / SCA: lands later through updates
                case .userCancelled:
                    completion("cancelled", nil)
                @unknown default:
                    completion("failed", "unknown purchase result")
                }
            } catch {
                let (outcome, detail) = Self.classify(error)
                completion(outcome, detail)
            }
        }
    }

    // MARK: - Restore

    /// `AppStore.sync()` (may ask the parent to sign in), then the current entitlement:
    /// restored, or nothingToRestore.
    @objc(restore:completion:)
    public func restore(productId: String, completion: @escaping OutcomeHandler) {
        Task {
            do {
                try await AppStore.sync()
            } catch {
                let (outcome, detail) = Self.classify(error)
                // A failed sync is the store being unreachable, not a failed purchase.
                let mapped = outcome == "failed" ? "storeUnavailable" : outcome
                Self.main { completion(mapped, detail) }
                return
            }
            var owned = false
            for await entitlement in Transaction.currentEntitlements(for: productId) {
                if case .verified(let transaction) = entitlement, transaction.revocationDate == nil {
                    owned = true
                    break
                }
            }
            let outcome = owned ? "restored" : "nothingToRestore"
            Self.main { completion(outcome, nil) }
        }
    }

    // MARK: - Helpers

    private func remember(_ product: Product) {
        lock.lock()
        products[product.id] = product
        lock.unlock()
    }

    private func cached(_ id: String) -> Product? {
        lock.lock()
        defer { lock.unlock() }
        return products[id]
    }

    private func product(_ id: String) async throws -> Product? {
        if let product = cached(id) { return product }
        let product = try await Product.products(for: [id]).first(where: { $0.id == id })
        if let product { remember(product) }
        return product
    }

    @MainActor
    private static func foregroundScene() -> UIWindowScene? {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        return scenes.first(where: { $0.activationState == .foregroundActive }) ?? scenes.first
    }

    /// StoreKit errors → outcome names.
    private static func classify(_ error: Error) -> (String, String?) {
        let detail = String(describing: error)
        if let storeKit = error as? StoreKitError {
            switch storeKit {
            case .userCancelled: return ("cancelled", nil)
            case .networkError, .systemError: return ("storeUnavailable", detail)
            case .notAvailableInStorefront: return ("notFound", detail)
            case .notEntitled: return ("failed", detail)
            default: return ("failed", detail)
            }
        }
        if let purchase = error as? Product.PurchaseError {
            switch purchase {
            case .productUnavailable: return ("notFound", detail)
            case .purchaseNotAllowed: return ("purchasesDisabled", detail)
            default: return ("failed", detail)
            }
        }
        return ("failed", detail)
    }

    private static func main(_ work: @escaping @Sendable () -> Void) {
        DispatchQueue.main.async(execute: work)
    }

    private static func json(_ object: [String: Any]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]),
              let text = String(data: data, encoding: .utf8) else { return "{}" }
        return text
    }
}
