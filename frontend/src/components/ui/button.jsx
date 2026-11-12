import React from "react";
import { Slot } from "@radix-ui/react-slot";
import { cva } from "class-variance-authority";
import { cn } from "../../lib/utils";
const variants=cva("inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-lg text-sm font-semibold transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:opacity-45 disabled:pointer-events-none",{
variants:{variant:{default:"bg-blue-600 text-white hover:bg-blue-700 shadow-sm",outline:"border border-slate-200 bg-white text-slate-700 hover:bg-slate-50",ghost:"text-slate-600 hover:bg-slate-100"},size:{default:"h-10 px-4 py-2",sm:"h-9 px-3",lg:"h-11 px-5"}},defaultVariants:{variant:"default",size:"default"}});
export function Button({className,variant,size,asChild=false,...props}){const Comp=asChild?Slot:"button";return <Comp className={cn(variants({variant,size}),className)} {...props}/>;}
