import { ClipboardList, Truck, Boxes } from 'lucide-react'
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs'
import { WorkDataSection } from './sections/WorkDataSection'
import { InFlightSection } from './sections/InFlightSection'
import { SortingSection } from './sections/SortingSection'

export function MonitorPage() {
  return (
    <Tabs defaultValue="work" className="flex flex-col gap-1">
      <TabsList>
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

      <TabsContent value="work">
        <WorkDataSection />
      </TabsContent>
      <TabsContent value="inflight">
        <InFlightSection />
      </TabsContent>
      <TabsContent value="sorting">
        <SortingSection />
      </TabsContent>
    </Tabs>
  )
}
