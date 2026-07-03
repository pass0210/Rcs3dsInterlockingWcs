import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { api } from './api'

// 폴링 주기(ms) — 집계·목록은 폴링(FRONTEND.md §2.3). SignalR 실시간 push는 F2.
// 프론트 폴 주기는 코드 상수 허용(계약 §0-22 — 백엔드 appsettings 대상 아님).
export const POLL_MS = 3000

export function useBatches() {
  return useQuery({
    queryKey: ['batches'],
    queryFn: () => api.batches(),
    refetchInterval: POLL_MS,
  })
}

export function useOrders(batchId?: number, status?: string) {
  return useQuery({
    queryKey: ['orders', batchId ?? null, status ?? null],
    queryFn: () => api.orders(batchId, status),
    enabled: batchId !== undefined,
    refetchInterval: POLL_MS,
    placeholderData: keepPreviousData,
  })
}

export function useOrderItems(orderId: number | null) {
  return useQuery({
    queryKey: ['orderItems', orderId],
    queryFn: () => api.orderItems(orderId as number),
    enabled: orderId !== null,
    refetchInterval: POLL_MS,
  })
}

export function useInFlight(cursor: number | null) {
  return useQuery({
    queryKey: ['inFlight', cursor],
    queryFn: () => api.inFlight(50, cursor),
    refetchInterval: POLL_MS,
    placeholderData: keepPreviousData,
  })
}

export function useSorters() {
  return useQuery({
    queryKey: ['sorters'],
    queryFn: () => api.sorters(),
    refetchInterval: POLL_MS,
  })
}

export function useCells(destId: number | null) {
  return useQuery({
    queryKey: ['cells', destId],
    queryFn: () => api.cells(destId as number),
    enabled: destId !== null,
    refetchInterval: POLL_MS,
  })
}

export function useSorterCommands(destId: number | null, cursor: number | null) {
  return useQuery({
    queryKey: ['sorterCommands', destId, cursor],
    queryFn: () => api.sorterCommands(destId ?? undefined, 50, cursor),
    enabled: destId !== null,
    refetchInterval: POLL_MS,
    placeholderData: keepPreviousData,
  })
}
