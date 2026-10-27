import { createFileRoute } from "@tanstack/react-router";
import { NeonDistrict } from "@/game/NeonDistrict";

export const Route = createFileRoute("/")({
  component: Home,
});

function Home() {
  return <NeonDistrict />;
}
