package main

import (
	"fmt"
	"os"

	"demo.local/gomini/internal/registry"
)

func main() {
	store := registry.New()
	store.Add(os.Args[1])
	store.Lookup("k")
	fmt.Println(store.Len(), registry.MaxSize)
}
