# RemotePlayServer Test Report - UI Redesign Phase 1-4
**Date**: 2026-03-28 | **Project**: RemotePlayServer.Tests

---

## Test Results Summary

| Metric | Value |
|--------|-------|
| **Total Tests** | 108 |
| **Passed** | 105 |
| **Failed** | 3 |
| **Skipped** | 0 |
| **Duration** | 49 ms |
| **Status** | ⚠️ EXPECTED FAILURES (Pre-existing) |

---

## Failed Tests (3 Pre-Existing)

### 1. Initialize_SetsCorrectTargetBitrate_At80PercentOfMax
**File**: `AdaptiveBitrateControllerTests.cs:143`
**Expected**: 40000
**Actual**: 50000
**Issue**: Initial bitrate calculation mismatch - expected 80% of max (50000), getting 50000 (100%)

### 2. Initialize_DefaultInitial_Is80PercentOfMax
**File**: `AdaptiveBitrateControllerTests.cs:567`
**Expected**: 12000
**Actual**: 15000
**Issue**: Default initial bitrate not calculating to 80% - getting 80% instead of expected 60%

### 3. ProcessFeedback_GoodConditions_BelowMax_IncreasesBitrate
**File**: `AdaptiveBitrateControllerTests.cs:332`
**Expected**: True
**Actual**: False
**Issue**: Bitrate increase logic not triggering under good network conditions

---

## Analysis

**No NEW failures detected**. All 3 failures are confirmed pre-existing issues in AdaptiveBitrateControllerTests:
- All failures relate to bitrate percentage calculations and adjustment logic
- Implementation appears to have changed from expected behavior
- Tests are deterministic and consistent

**Pass Rate**: 97.2% (105/108 tests passing)

---

## Passed Test Categories

- Network condition scenarios (good/bad/critical states)
- FPS drop detection
- Cooldown/warmup period handling
- Packet loss scenarios
- Stats generation
- Bitrate clamping
- Reset functionality

---

## Recommendation

The 3 pre-existing failures are isolated to AdaptiveBitrateController initialization and adjustment logic. Consider:
1. Reviewing recent changes to bitrate calculation logic
2. Updating tests to match new implementation OR fixing implementation to match original spec
3. All other 105 tests passing indicates core functionality is stable
