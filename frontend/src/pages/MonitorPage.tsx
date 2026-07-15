import { ClipboardList, Truck, Boxes } from 'lucide-react'
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs'
import { WorkDataSection } from './sections/WorkDataSection'
import { InFlightSection } from './sections/InFlightSection'
import { SortingSection } from './sections/SortingSection'

export function MonitorPage() {
  // 뷰포트 맞춤(S-UI-LAYOUT) — Tabs 컨테이너가 가용 높이를 채우고(flex-1 min-h-0), 탭바는 고정 크롬,
  // 활성 TabsContent 가 flex 본문이 되어 각 섹션의 그리드 본문만 스크롤한다(mt-0 로 기본 상단여백 제거).
  return (
    <Tabs defaultValue="work" className="flex min-h-0 flex-1 flex-col gap-3">
      <TabsList className="shrink-0">
        <TabsTrigger value="work">
          <ClipboardList className="size-4" />
          작업 데이터
        </TabsTrigger>
        <TabsTrigger value="inflight">
          <Truck className="size-4" />
          로봇 이동중
        </TabsTrigger>
        <TabsTrigger value="sorting">
          <Boxes className="size-4" />
          분류 현황
        </TabsTrigger>
      </TabsList>

      <TabsContent value="work" className="mt-0 flex min-h-0 flex-1 flex-col">
        <WorkDataSection />
      </TabsContent>
      <TabsContent value="inflight" className="mt-0 flex min-h-0 flex-1 flex-col">
        <InFlightSection />
      </TabsContent>
      <TabsContent value="sorting" className="mt-0 flex min-h-0 flex-1 flex-col">
        <SortingSection />
      </TabsContent>
    </Tabs>
  )
}
